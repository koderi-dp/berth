using System.Buffers;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Dispatching;

namespace Berth.Pages.Detail;

/// <summary>
/// The app's end of the byte path from a shell to the terminal page. Each chunk the session's pump hands over is copied into one
/// buffer and posted on the UI thread as an <c>output</c> message (chunks that arrive while a post is pending go out together). Every
/// byte is counted in a <see cref="TerminalFlow"/>: <see cref="WriteAsync"/> completes only while the flow is not paused, so a shell
/// that floods output waits on xterm's acknowledgements instead of piling bytes up. A new shell (a higher generation, from the
/// session's pump or from <see cref="Begin"/>) clears the screen first and starts the count again.
/// </summary>
/// <remarks><see cref="WriteAsync"/> is called on a thread-pool thread; everything else on the UI thread.</remarks>
internal sealed class TerminalBridge(DispatcherQueue ui) : IExecOutput
{
    private readonly Lock _lock = new();
    private readonly TerminalFlow _flow = new();
    private readonly ArrayBufferWriter<byte> _pending = new();
    private TerminalHandle? _handle;
    private TaskCompletionSource? _resume;
    private int _generation;
    private bool _resetPending;
    private bool _postQueued;

    /// <summary>The shell whose output is shown: a higher generation from the pump starts a new screen, a lower one is dropped.</summary>
    public int Generation
    {
        get
        {
            lock (_lock)
                return _generation;
        }
    }

    /// <summary>A new terminal page (a new WebView, or the same one loaded again): nothing it was sent is outstanding.</summary>
    public void Connect(TerminalHandle handle)
    {
        TaskCompletionSource? resume;
        lock (_lock)
        {
            _handle = handle;
            _flow.Reset();
            resume = TakeResume();
        }

        resume?.TrySetResult();
    }

    /// <summary>The terminal page is gone: output is dropped until the next one connects, and the pump is not held up.</summary>
    public void Disconnect(TerminalHandle handle)
    {
        TaskCompletionSource? resume;
        lock (_lock)
        {
            if (_handle != handle)
                return;
            _handle = null;
            _pending.ResetWrittenCount();
            _flow.Reset();
            resume = TakeResume();
        }

        resume?.TrySetResult();
    }

    /// <summary>The shell of <paramref name="generation"/> is starting: the screen is cleared before anything of it is shown.</summary>
    public void Begin(int generation)
    {
        TaskCompletionSource? resume;
        lock (_lock)
        {
            if (generation <= _generation)
                return;
            resume = StartGeneration(generation);
            QueuePost();
        }

        resume?.TrySetResult();
    }

    /// <summary>xterm has written <paramref name="bytes"/> of output.</summary>
    public void Acked(int bytes)
    {
        TaskCompletionSource? resume = null;
        lock (_lock)
        {
            _flow.Acked(bytes);
            if (!_flow.Paused)
                resume = TakeResume();
        }

        resume?.TrySetResult();
    }

    public ValueTask WriteAsync(int generation, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        TaskCompletionSource? released = null;
        Task wait;
        lock (_lock)
        {
            if (generation < _generation || _handle is null)
                return ValueTask.CompletedTask;
            if (generation > _generation)
                released = StartGeneration(generation);

            _pending.Write(bytes.Span);
            _flow.Sent(bytes.Length);
            QueuePost();
            if (!_flow.Paused)
            {
                released?.TrySetResult();
                return ValueTask.CompletedTask;
            }

            _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = _resume.Task;
        }

        released?.TrySetResult();
        return new ValueTask(wait.WaitAsync(ct));
    }

    // Under the lock.
    private TaskCompletionSource? StartGeneration(int generation)
    {
        _generation = generation;
        _resetPending = true;
        _pending.ResetWrittenCount();
        _flow.Reset();
        return TakeResume();
    }

    // Under the lock.
    private TaskCompletionSource? TakeResume()
    {
        var resume = _resume;
        _resume = null;
        return resume;
    }

    // Under the lock: one post at a time is queued; what arrives meanwhile goes out with it.
    private void QueuePost()
    {
        if (_postQueued)
            return;
        _postQueued = ui.TryEnqueue(Flush);
    }

    private void Flush()
    {
        string? output = null;
        bool reset;
        TerminalHandle? handle;
        lock (_lock)
        {
            _postQueued = false;
            handle = _handle;
            reset = _resetPending;
            _resetPending = false;
            if (_pending.WrittenCount > 0)
            {
                output = TerminalProtocol.Output(_pending.WrittenSpan);
                _pending.ResetWrittenCount();
            }
        }

        if (handle is null)
            return;
        if (reset)
            handle.Post(TerminalProtocol.Reset());
        if (output is not null)
            handle.Post(output);
    }
}

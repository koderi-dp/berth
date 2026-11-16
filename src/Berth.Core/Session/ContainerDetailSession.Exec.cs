using System.Collections.Immutable;
using System.Threading.Channels;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Session;

/// <summary>
/// The Exec tab's shell: create and start it, then pump bytes both ways between its stream and the attached
/// <see cref="IExecOutput"/>. Output waits on the terminal (back-pressure); input has its own loop, so a keystroke never waits
/// behind output. Only the status goes to the state; no byte and no command is formatted, logged or kept.
/// </summary>
/// <remarks>
/// Ending a shell on purpose (a stop, the session disposed or cut off): the input loop is stopped first, then the stream's write
/// side is closed where the connection has one (TCP, TLS), so the shell reads the end of its input and can exit by itself; the
/// stream is disposed once the engine ends it or after <see cref="ExecCloseGrace"/>. Where there is no write side to close (Docker
/// Desktop's pipe) the stream is disposed at once. Nothing a stopped shell still sends reaches the terminal or the state.
/// </remarks>
public sealed partial class ContainerDetailSession
{
    /// <summary>How long after the stream ended the exec is inspected again while the engine still says it runs.</summary>
    public static readonly TimeSpan ExecPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a shell whose write side was closed has to end its stream before the stream is disposed.</summary>
    public static readonly TimeSpan ExecCloseGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>How many times in all the exec is inspected for its exit code after the stream ended.</summary>
    private const int ExecInspectTries = 5;

    private const int ExecReadSize = 32 * 1024;

    /// <summary>
    /// What a shell's failure shows when the terminal it writes to (the attached <see cref="IExecOutput"/>) threw: Berth's words,
    /// never that exception's message, which is not the engine's and could hold terminal bytes.
    /// </summary>
    public const string TerminalFailedText = "The terminal stopped working. Start again.";

    private const string ExecFailedReport = "the exec stream failed";
    private const string TerminalFailedReport = "the terminal failed";
    private const string ResizeFailedReport = "resizing the terminal failed";
    private const string ExitCodeFailedReport = "reading the shell's exit code failed";
    private const string CleanUpFailedReport = "ending a shell left in the container failed";

    /// <summary>How long the clean-up after a shell (<see cref="ExecCleanUp"/>) may take before it is given up.</summary>
    public static readonly TimeSpan ExecCleanUpLimit = TimeSpan.FromSeconds(3);

    // The clean-ups under way; a dispose or cut-off waits for them, so the engine client is not disposed under one.
    private readonly Lock _cleanUpsLock = new();
    private readonly List<Task> _cleanUps = [];

    private IExecOutput? _execOutput;

    private readonly Lock _resizeLock = new();

    // The resize to send next, and whether a sender is running; both under _resizeLock.
    private ResizeExecEffect? _nextResize;
    private bool _resizing;

    // The latest shell asked for; written by the loop, read by any thread.
    private ExecRun? _exec;

    /// <summary>The bytes waiting to be written to the current shell (<see cref="ExecInput.QueueLimit"/> at most). For tests.</summary>
    internal long QueuedExecInput => Volatile.Read(ref _exec)?.Queued ?? 0;

    /// <summary>Raised each time the exec runtime starts waiting on the clock (between inspects, or the close grace). For tests.</summary>
    internal event Action? ExecWaiting;

    /// <summary>Opens a shell running <paramref name="command"/> when the container runs and no shell is starting or running.</summary>
    public void StartExec(ImmutableArray<string> command, TerminalSize size) => Post(new ExecStartRequested(command, size));

    /// <summary>Ends the shell on purpose (nothing more of it is shown), or clears how the last one ended.</summary>
    public void StopExec() => Post(new ExecStopRequested());

    /// <summary>Records the terminal's size; sent to a running shell, or once a starting one runs. Invalid sizes are ignored.</summary>
    public void ResizeExec(TerminalSize size) => Post(new ExecResizeRequested(size));

    /// <summary>
    /// Sends what was typed or pasted to the shell of <paramref name="generation"/>. The bytes are copied. Dropped unless that shell
    /// is the latest one and its stream is open, and dropped whole when it would take the bytes waiting for the shell past
    /// <see cref="ExecInput.QueueLimit"/>, which the result says.
    /// </summary>
    public ExecInputOutcome SendExecInput(int generation, ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty)
            return ExecInputOutcome.Sent;
        return Volatile.Read(ref _exec) is { } run && run.Generation == generation ? run.Send(bytes.ToArray()) : ExecInputOutcome.Ignored;
    }

    /// <summary>Sets the terminal the shell's output goes to, or none: output that arrives with none attached is dropped.</summary>
    public void AttachExecOutput(IExecOutput? output) => Volatile.Write(ref _execOutput, output);

    /// <summary>Asks the current shell, if any, to end on purpose. Safe from any thread.</summary>
    private void StopExecRun() => Volatile.Read(ref _exec)?.RequestStop();

    private void StartShell(StartExecEffect start)
    {
        var run = new ExecRun(start.Generation, ExecCleanUp.NewMarker());
        Volatile.Write(ref _exec, run);
        Run(ct => RunExecAsync(run, start, ct), _calls.Token);
    }

    /// <summary>
    /// Starts the clean-up after the shell of <paramref name="generation"/>, once, when it is still the latest one. After a start
    /// (<see cref="CleanUpExecEffect.AfterStart"/>) it first waits, within the same limit, for the shell's create and start to return,
    /// and runs only when the create gave an exec.
    /// </summary>
    private void StartCleanUp(int generation, bool afterStart)
    {
        if (Volatile.Read(ref _exec) is not { } run || run.Generation != generation || !run.TakeCleanUp())
            return;

        var containerId = State.ContainerId;
        lock (_cleanUpsLock)
        {
            _cleanUps.RemoveAll(t => t.IsCompleted);
            _cleanUps.Add(Task.Run(() => CleanUpAsync(containerId, run, afterStart)));
        }
    }

    /// <summary>
    /// The page or the connection is going away with a shell still asked for (the tab's own stop may not have been handled): the
    /// same decision as a stop (<see cref="DetailReducer.ShellNeedsCleanUp"/>) on the state as it is now.
    /// </summary>
    private void CleanUpLeftShell()
    {
        var state = State;
        if (DetailReducer.ShellNeedsCleanUp(state))
            StartCleanUp(state.Exec.Generation, afterStart: state.Exec.Status == ExecStatus.Starting);
    }

    /// <summary>Waits for the clean-ups under way; each one ends within <see cref="ExecCleanUpLimit"/>.</summary>
    private Task WaitForCleanUpsAsync()
    {
        Task[] cleanUps;
        lock (_cleanUpsLock)
            cleanUps = [.. _cleanUps];
        return Task.WhenAll(cleanUps);
    }

    /// <summary>
    /// Runs the clean-up as a detached exec, under its own limit rather than the session's calls (a dispose or cut-off stops those
    /// and still wants it). A failure (no <c>sh</c> or <c>/proc</c> in the image, the engine refusing, the connection gone) is only
    /// reported, in fixed words: the shell was already ended as far as the page is concerned.
    /// </summary>
    private async Task CleanUpAsync(string containerId, ExecRun run, bool afterStart)
    {
        using var limit = new CancellationTokenSource(ExecCleanUpLimit, _time);
        try
        {
            if (afterStart && !await run.Created.WaitAsync(limit.Token).ConfigureAwait(false))
                return;
            await _api.RunDetachedExecAsync(containerId, ExecCleanUp.Command(run.Marker), limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(CleanUpFailedReport, ex);
        }
    }

    /// <summary>
    /// Sends resizes one at a time, so the engine gets them in order; a resize asked for while one is being sent replaces any
    /// other still waiting, so only the newest size follows.
    /// </summary>
    private void StartResize(ResizeExecEffect resize)
    {
        lock (_resizeLock)
        {
            _nextResize = resize;
            if (_resizing)
                return;
            _resizing = true;
        }

        Run(SendResizesAsync, _calls.Token);
    }

    private async Task SendResizesAsync(CancellationToken ct)
    {
        while (true)
        {
            ResizeExecEffect resize;
            lock (_resizeLock)
            {
                if (_nextResize is null || ct.IsCancellationRequested)
                {
                    _nextResize = null;
                    _resizing = false;
                    return;
                }

                resize = _nextResize;
                _nextResize = null;
            }

            try
            {
                await _api.ResizeExecAsync(resize.ExecId, resize.Size, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                // Not shown: a resize fails when the shell is ending, and its end is what the tab shows.
                Report(ResizeFailedReport, ex);
            }
        }
    }

    /// <summary>One shell from create to dispose. Its loops run under a scope linked to the session's calls, so a cut-off stops it.</summary>
    private async Task RunExecAsync(ExecRun run, StartExecEffect start, CancellationToken calls)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(calls);
        using var inputScope = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);
        if (!run.Open(scope))
        {
            run.Settle(created: false);
            return;
        }

        var ct = scope.Token;
        ExecStream? stream = null;
        var output = Task.CompletedTask;
        var input = Task.CompletedTask;
        var closeTried = false;
        string? execId = null;
        try
        {
            execId = await _api.CreateExecAsync(
                State.ContainerId, start.Command, start.Size, [ExecCleanUp.Variable(run.Marker)], ct).ConfigureAwait(false);
            try
            {
                stream = await _api.StartExecAsync(execId, start.Size, ct).ConfigureAwait(false);
            }
            finally
            {
                run.Settle(created: true);
            }

            run.MarkStarted();
            Post(new ExecStarted(run.Generation, execId));

            output = PumpOutputAsync(run, stream, ct);
            input = PumpInputAsync(run, stream, inputScope.Token);
            var first = await Task.WhenAny(output, input, run.StopRequested).ConfigureAwait(false);
            if (first == run.StopRequested)
            {
                // The write side is closed only once the input loop has stopped: on TLS closing it is itself a write.
                await StopInputAsync(inputScope, input).ConfigureAwait(false);
                closeTried = true;
                if (await TryCloseWriteAsync(stream).ConfigureAwait(false))
                {
                    var grace = Task.Delay(ExecCloseGrace, _time, ct);
                    ExecWaiting?.Invoke();
                    await Task.WhenAny(output, grace).ConfigureAwait(false);
                }

                return;
            }

            // The input loop ends early only by failing; the output loop ends when the engine closes the stream.
            await first.ConfigureAwait(false);
            await StopInputAsync(inputScope, input).ConfigureAwait(false);
            var (exitCode, stillRunning) = await ExitCodeAsync(execId, run, ct).ConfigureAwait(false);
            Post(new ExecEnded(run.Generation, exitCode, run.HadInput, stillRunning));
        }
        catch (Exception) when (ct.IsCancellationRequested || run.IsStopRequested)
        {
            // Stopped on purpose: nothing is shown or reported.
        }
        catch (TerminalFailedException ex)
        {
            Report(TerminalFailedReport, ex.InnerException!);
            Post(new ExecFailed(run.Generation, TerminalFailedText));
        }
        catch (Exception ex)
        {
            Report(ExecFailedReport, ex);
            Post(new ExecFailed(run.Generation, ex.Message));
        }
        finally
        {
            run.Settle(created: execId is not null);
            run.Finish();
            await StopInputAsync(inputScope, input).ConfigureAwait(false);
            if (stream is not null)
            {
                // A dispose or cut-off that raced the stop still lets the shell read the end of its input.
                if (run.IsStopRequested && !closeTried)
                    await TryCloseWriteAsync(stream).ConfigureAwait(false);
                await scope.CancelAsync().ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                await Quietly(output).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads the shell's output and hands it to the terminal, waiting for it each time, until the engine ends the stream.</summary>
    private async Task PumpOutputAsync(ExecRun run, ExecStream stream, CancellationToken ct)
    {
        var buffer = new byte[ExecReadSize];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                return;
            if (run.IsStopRequested || Volatile.Read(ref _execOutput) is not { } terminal)
                continue;
            try
            {
                await terminal.WriteAsync(run.Generation, buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                throw new TerminalFailedException(ex);
            }
        }
    }

    /// <summary>Writes what was typed to the shell, in order, until the shell ends or is stopped.</summary>
    private static async Task PumpInputAsync(ExecRun run, ExecStream stream, CancellationToken ct)
    {
        await foreach (var bytes in run.Input.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            run.Written(bytes.Length);
        }
    }

    private static async Task StopInputAsync(CancellationTokenSource inputScope, Task input)
    {
        await inputScope.CancelAsync().ConfigureAwait(false);
        await Quietly(input).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the stream's write side; false when it has none, or closing failed or outlasted the grace (the stream is disposed then
    /// anyway). A close-write left behind (TLS's can wait on a shell that reads nothing) is observed, so its failure once the stream
    /// is disposed under it is not left unobserved.
    /// </summary>
    private async Task<bool> TryCloseWriteAsync(ExecStream stream)
    {
        Task<bool>? closing = null;
        try
        {
            closing = stream.CloseWriteAsync();
            return await closing.WaitAsync(ExecCloseGrace, _time).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (closing is { IsCompleted: false })
            {
                _ = closing.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return false;
        }
    }

    /// <summary>
    /// The exit code of an exec whose stream ended: inspected at once, then every <see cref="ExecPollInterval"/> while the engine
    /// still says it runs, at most <see cref="ExecInspectTries"/> times in all. Null when the engine never gives one; still running
    /// when the last inspect said so (the stream was detached, not ended by the shell). A stop ends it (cancelled, so nothing is
    /// posted or reported).
    /// </summary>
    private async Task<(int? ExitCode, bool StillRunning)> ExitCodeAsync(string execId, ExecRun run, CancellationToken calls)
    {
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(calls, run.Stopping);
        var ct = poll.Token;
        for (var tries = 1; ; tries++)
        {
            ExecInspection inspection;
            try
            {
                inspection = await _api.InspectExecAsync(execId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The shell has ended either way; only its code is unknown.
                Report(ExitCodeFailedReport, ex);
                return (null, false);
            }

            if (!inspection.Running)
                return (inspection.ExitCode, false);
            if (tries == ExecInspectTries)
                return (null, true);

            var wait = Task.Delay(ExecPollInterval, _time, ct);
            ExecWaiting?.Invoke();
            await wait.ConfigureAwait(false);
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Its failure, if it mattered, was reported by whoever awaited it first.
        }
    }

    /// <summary>One shell asked for: its generation, its input queue, and whether it has been asked to stop.</summary>
    private sealed class ExecRun(int generation, string marker)
    {
        private readonly Lock _lock = new();
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stopping = new();
        private CancellationTokenSource? _scope;
        private bool _started;
        private bool _finished;
        private long _queued;
        private volatile bool _hadInput;
        private int _cleanUpTaken;

        public int Generation => generation;

        /// <summary>The shell's <see cref="ExecCleanUp"/> marker. Never logged.</summary>
        public string Marker => marker;

        /// <summary>True the first time only: the clean-up after this shell runs once, whoever asks for it.</summary>
        public bool TakeCleanUp() => Interlocked.Exchange(ref _cleanUpTaken, 1) == 0;

        /// <summary>
        /// Completes once the create and start have returned or failed: true when the create gave an exec (the engine may have started
        /// the shell even when the start was cancelled), false when there is none.
        /// </summary>
        public Task<bool> Created => _created.Task;

        /// <summary>Settles <see cref="Created"/>; the first call wins.</summary>
        public void Settle(bool created) => _created.TrySetResult(created);

        /// <summary>Completes when the shell is asked to end on purpose.</summary>
        public Task StopRequested => _stop.Task;

        public bool IsStopRequested => _stop.Task.IsCompleted;

        /// <summary>Cancelled when the shell is asked to end on purpose.</summary>
        public CancellationToken Stopping => _stopping.Token;

        public bool HadInput => _hadInput;

        public ChannelReader<byte[]> Input => _input.Reader;

        /// <summary>The bytes taken and not yet written, the write under way included.</summary>
        public long Queued
        {
            get
            {
                lock (_lock)
                    return _queued;
            }
        }

        /// <summary>Takes the scope the shell runs under; false when it was stopped before it began.</summary>
        public bool Open(CancellationTokenSource scope)
        {
            lock (_lock)
            {
                if (IsStopRequested)
                    return false;
                _scope = scope;
                return true;
            }
        }

        /// <summary>The stream is open: input is taken from now on.</summary>
        public void MarkStarted()
        {
            lock (_lock)
                _started = true;
        }

        public ExecInputOutcome Send(byte[] bytes)
        {
            lock (_lock)
            {
                if (!_started || _finished || IsStopRequested)
                    return ExecInputOutcome.Ignored;
                if (!ExecInput.Accepts(_queued, bytes.Length))
                    return ExecInputOutcome.Dropped;
                if (!_input.Writer.TryWrite(bytes))
                    return ExecInputOutcome.Ignored;
                _queued += bytes.Length;
                _hadInput = true;
                return ExecInputOutcome.Sent;
            }
        }

        /// <summary>A message of <paramref name="length"/> bytes has been written: it no longer counts against the limit.</summary>
        public void Written(int length)
        {
            lock (_lock)
                _queued -= length;
        }

        /// <summary>
        /// Asks the shell to end. Before its stream is open the create or start is cancelled; after, the run closes it in order
        /// (see <see cref="ContainerDetailSession"/>'s remarks).
        /// </summary>
        public void RequestStop()
        {
            CancellationTokenSource? cancel;
            lock (_lock)
            {
                if (!_stop.TrySetResult())
                    return;
                cancel = _started || _finished ? null : _scope;
            }

            _stopping.Cancel();
            try
            {
                cancel?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run ended meanwhile.
            }
        }

        /// <summary>The run is ending: no more input, and its scope is about to be disposed.</summary>
        public void Finish()
        {
            lock (_lock)
            {
                _finished = true;
                _scope = null;
                _input.Writer.TryComplete();
            }
        }
    }
}

/// <summary>Carries a failure of the attached terminal out of the output pump, so it is not taken for the engine's.</summary>
internal sealed class TerminalFailedException(Exception inner) : Exception(null, inner);

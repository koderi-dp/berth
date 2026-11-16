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

    private const string ExecFailedReport = "the exec stream failed";
    private const string ResizeFailedReport = "resizing the terminal failed";
    private const string ExitCodeFailedReport = "reading the shell's exit code failed";

    private IExecOutput? _execOutput;

    // The latest shell asked for; written by the loop, read by any thread.
    private ExecRun? _exec;

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
    /// is the latest one and its stream is open.
    /// </summary>
    public void SendExecInput(int generation, ReadOnlyMemory<byte> bytes)
    {
        if (!bytes.IsEmpty && Volatile.Read(ref _exec) is { } run && run.Generation == generation)
            run.Send(bytes.ToArray());
    }

    /// <summary>Sets the terminal the shell's output goes to, or none: output that arrives with none attached is dropped.</summary>
    public void AttachExecOutput(IExecOutput? output) => Volatile.Write(ref _execOutput, output);

    /// <summary>Asks the current shell, if any, to end on purpose. Safe from any thread.</summary>
    private void StopExecRun() => Volatile.Read(ref _exec)?.RequestStop();

    private void StartShell(StartExecEffect start)
    {
        var run = new ExecRun(start.Generation);
        Volatile.Write(ref _exec, run);
        Run(ct => RunExecAsync(run, start, ct), _calls.Token);
    }

    private void StartResize(ResizeExecEffect resize) => Run(ct => ResizeAsync(resize, ct), _calls.Token);

    private async Task ResizeAsync(ResizeExecEffect resize, CancellationToken ct)
    {
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

    /// <summary>One shell from create to dispose. Its loops run under a scope linked to the session's calls, so a cut-off stops it.</summary>
    private async Task RunExecAsync(ExecRun run, StartExecEffect start, CancellationToken calls)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(calls);
        using var inputScope = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);
        if (!run.Open(scope))
            return;

        var ct = scope.Token;
        ExecStream? stream = null;
        var output = Task.CompletedTask;
        var input = Task.CompletedTask;
        var closeTried = false;
        try
        {
            var execId = await _api.CreateExecAsync(State.ContainerId, start.Command, start.Size, ct).ConfigureAwait(false);
            stream = await _api.StartExecAsync(execId, start.Size, ct).ConfigureAwait(false);
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
            var exitCode = await ExitCodeAsync(execId, ct).ConfigureAwait(false);
            Post(new ExecEnded(run.Generation, exitCode, run.HadInput));
        }
        catch (Exception) when (ct.IsCancellationRequested || run.IsStopRequested)
        {
            // Stopped on purpose: nothing is shown or reported.
        }
        catch (Exception ex)
        {
            Report(ExecFailedReport, ex);
            Post(new ExecFailed(run.Generation, ex.Message));
        }
        finally
        {
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
            await terminal.WriteAsync(run.Generation, buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Writes what was typed to the shell, in order, until the shell ends or is stopped.</summary>
    private static async Task PumpInputAsync(ExecRun run, ExecStream stream, CancellationToken ct)
    {
        await foreach (var bytes in run.Input.ReadAllAsync(ct).ConfigureAwait(false))
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static async Task StopInputAsync(CancellationTokenSource inputScope, Task input)
    {
        await inputScope.CancelAsync().ConfigureAwait(false);
        await Quietly(input).ConfigureAwait(false);
    }

    /// <summary>Closes the stream's write side; false when it has none or closing it failed (the stream is disposed then anyway).</summary>
    private async Task<bool> TryCloseWriteAsync(ExecStream stream)
    {
        try
        {
            return await stream.CloseWriteAsync().WaitAsync(ExecCloseGrace, _time).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The exit code of an exec whose stream ended: inspected at once, then every <see cref="ExecPollInterval"/> while the engine
    /// still says it runs, at most <see cref="ExecInspectTries"/> times in all. Null when the engine never gives one.
    /// </summary>
    private async Task<int?> ExitCodeAsync(string execId, CancellationToken ct)
    {
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
                return null;
            }

            if (!inspection.Running)
                return inspection.ExitCode;
            if (tries == ExecInspectTries)
                return null;

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
    private sealed class ExecRun(int generation)
    {
        private readonly Lock _lock = new();
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _scope;
        private bool _started;
        private bool _finished;
        private volatile bool _hadInput;

        public int Generation => generation;

        /// <summary>Completes when the shell is asked to end on purpose.</summary>
        public Task StopRequested => _stop.Task;

        public bool IsStopRequested => _stop.Task.IsCompleted;

        public bool HadInput => _hadInput;

        public ChannelReader<byte[]> Input => _input.Reader;

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

        public void Send(byte[] bytes)
        {
            lock (_lock)
            {
                if (!_started || _finished || IsStopRequested)
                    return;
                if (_input.Writer.TryWrite(bytes))
                    _hadInput = true;
            }
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

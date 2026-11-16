using System.Threading.Channels;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Session;

/// <summary>
/// Owns one container's detail page: feeds each event through <see cref="DetailReducer"/> on a single loop and carries out the
/// effects it returns (inspect, the log stream, the stats stream and the Exec tab's shell) against <see cref="IEngineApi"/>. Behaviour lives in the
/// reducer; this class only runs it.
/// </summary>
/// <remarks>
/// Results carry the inspect or stream generation that asked for them, and the reducer drops older ones, so a stream stopped
/// on purpose never reaches the page. Log lines are collected and applied at most every <see cref="LogTick"/>, so a flood is
/// one state change per tick. The session uses the engine client it is given and never disposes it.
/// </remarks>
public sealed partial class ContainerDetailSession : IAsyncDisposable
{
    /// <summary>How long log lines are collected before they are applied together.</summary>
    public static readonly TimeSpan LogTick = TimeSpan.FromMilliseconds(100);

    // What failed, as told to the error sink. Fixed wording: the exception carries the rest.
    private const string InspectFailedReport = "reading the container failed";
    private const string LogsFailedReport = "the container log stream failed";
    private const string StatsFailedReport = "the container stats stream failed";
    private const string HandlerFailedReport = "a detail state change handler failed";
    private const string LoopFailedReport = "handling a detail event failed";

    private readonly IEngineApi _api;
    private readonly TimeProvider _time;
    private readonly Action<string, Exception>? _onError;
    private readonly Channel<DetailEvent> _events = Channel.CreateUnbounded<DetailEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    private DetailState _state;
    private int _disposed;
    private Action? _release;

    // Every engine call runs under _calls; once it is cancelled (stopped) no new call starts. Guarded by _callsLock.
    private readonly Lock _callsLock = new();
    private readonly CancellationTokenSource _calls = new();
    private readonly List<Task> _running = [];
    private bool _stopped;

    // Touched only by the loop, and by DisposeAsync once the loop has ended.
    private CancellationTokenSource? _streams;
    private readonly List<CancellationTokenSource> _spentStreams = [];

    /// <param name="api">The engine client of the current connection; used, never disposed.</param>
    /// <param name="onError">
    /// Told what failed, in fixed wording, and the exception: a failed inspect (other than the container being gone), a failed
    /// log or stats stream, a failed shell, resize or exit-code read, a throwing <see cref="StateChanged"/> handler, or an event the loop could not handle. Never told of
    /// a cancellation the session caused. Called on a thread-pool thread; an exception it throws is ignored.
    /// </param>
    public ContainerDetailSession(IEngineApi api, string containerId, TimeProvider time, Action<string, Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(containerId);
        ArgumentNullException.ThrowIfNull(time);
        _api = api;
        _time = time;
        _onError = onError;
        _state = DetailState.Initial(containerId);
        _loop = Task.Run(RunAsync);
    }

    /// <summary>The current state; safe to read from any thread.</summary>
    public DetailState State => Volatile.Read(ref _state);

    /// <summary>Raised on a thread-pool thread, in order, each time the state changes.</summary>
    public event Action<DetailState>? StateChanged;

    /// <summary>Raised with the number of log lines waiting for the next tick each time one is collected. For tests.</summary>
    internal event Action<int>? LinesCollected;

    /// <summary>Reads the container, then follows its log (the last 1000 lines first) and, while it runs, its stats.</summary>
    public void Open() => Post(new DetailOpenRequested());

    /// <summary>Reads the container again; restarts the streams when it was started again since the last read.</summary>
    public void Refresh() => Post(new DetailRefreshRequested());

    /// <summary>Empties the log view; lines that arrive later are shown.</summary>
    public void ClearLogs() => Post(new LogsClearRequested());

    /// <summary>
    /// Cancels every call and stream, and ends a shell still asked for in the container (<see cref="ExecCleanUp"/>), waiting at most
    /// <see cref="ExecCleanUpLimit"/> for that. No <see cref="StateChanged"/> is raised after this returns.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _events.Writer.TryComplete();
        _lifetime.Cancel();
        await _loop.ConfigureAwait(false);
        StopExecRun();
        CleanUpLeftShell();

        // Still tracked by the connection until the clean-ups end, so a connection retired meanwhile waits for them before it
        // disposes the engine client.
        await WaitForCleanUpsAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _release, null)?.Invoke();
        await StopCallsAsync().ConfigureAwait(false);

        foreach (var scope in _spentStreams)
            scope.Dispose();
        _streams?.Dispose();
        _calls.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>
    /// Ties the session to the engine connection it runs on: <paramref name="release"/> is called when the page disposes the
    /// session first, so the connection stops tracking it.
    /// </summary>
    internal void ReleaseOnDispose(Action release) => _release = release;

    /// <summary>
    /// The connection this session runs on is being retired: stops every call, waits for them to finish (so the engine client
    /// can be disposed after), and marks the state as lost.
    /// </summary>
    internal async Task CutOffAsync()
    {
        StopExecRun();
        CleanUpLeftShell();
        await WaitForCleanUpsAsync().ConfigureAwait(false);
        await StopCallsAsync().ConfigureAwait(false);
        Post(new DetailConnectionLost());
    }

    private void Post(DetailEvent e) => _events.Writer.TryWrite(e);

    private async Task RunAsync()
    {
        try
        {
            await foreach (var e in _events.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    Handle(e);
                }
                catch (Exception ex)
                {
                    // A throwing reducer or effect start must not end the loop: the page would freeze on its last state.
                    Report(LoopFailedReport, ex);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    /// <summary>Runs one event through the reducer, starts its effects, then tells observers of the new state.</summary>
    private void Handle(DetailEvent e)
    {
        var (next, effects) = DetailReducer.Reduce(_state, e);
        var changed = !ReferenceEquals(next, _state);
        Volatile.Write(ref _state, next);

        try
        {
            foreach (var effect in effects)
                Start(effect);
        }
        finally
        {
            if (changed)
                Raise(next);
        }
    }

    private void Raise(DetailState state)
    {
        if (StateChanged is not { } handlers)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<DetailState>>())
        {
            try
            {
                handler(state);
            }
            catch (Exception ex)
            {
                Report(HandlerFailedReport, ex);
            }
        }
    }

    /// <summary>Tells the error sink, if any. A throwing sink is ignored: reporting must not break what it reports on.</summary>
    private void Report(string what, Exception ex)
    {
        try
        {
            _onError?.Invoke(what, ex);
        }
        catch (Exception)
        {
            // Nowhere left to report to.
        }
    }

    private void Start(DetailEffect effect)
    {
        switch (effect)
        {
            case InspectContainerEffect inspect:
                Run(ct => InspectAsync(inspect.Generation, ct), _calls.Token);
                break;

            case FollowLogsEffect logs:
                Run(ct => FollowLogsAsync(logs, ct), StreamToken());
                break;

            case FollowStatsEffect stats:
                Run(ct => FollowStatsAsync(stats.Generation, ct), StreamToken());
                break;

            case StopDetailStreamsEffect when _streams is { } streams:
                streams.Cancel();
                _spentStreams.Add(streams);
                _streams = null;
                break;

            case StartExecEffect start:
                StartShell(start);
                break;

            case StopExecEffect:
                StopExecRun();
                break;

            case CleanUpExecEffect cleanUp:
                StartCleanUp(cleanUp.Generation);
                break;

            case ResizeExecEffect resize:
                StartResize(resize);
                break;
        }
    }

    /// <summary>The token of the current streams, shared by the log and stats stream of one generation.</summary>
    private CancellationToken StreamToken()
    {
        _streams ??= CancellationTokenSource.CreateLinkedTokenSource(_calls.Token);
        return _streams.Token;
    }

    /// <summary>Runs <paramref name="call"/> on the thread pool, unless the calls were stopped.</summary>
    private void Run(Func<CancellationToken, Task> call, CancellationToken token)
    {
        lock (_callsLock)
        {
            if (_stopped)
                return;
            _running.RemoveAll(t => t.IsCompleted);
            _running.Add(Task.Run(() => call(token)));
        }
    }

    /// <summary>Cancels every call, lets no new one start, and waits for those running to finish.</summary>
    private async Task StopCallsAsync()
    {
        Task[] running;
        lock (_callsLock)
        {
            // Cancelled under the lock, so a dispose racing a cut-off cannot dispose the source before it is cancelled. A
            // call's cancellation path never takes this lock.
            if (!_stopped)
            {
                _stopped = true;
                _calls.Cancel();
            }

            running = [.. _running];
        }

        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Calls report their own failures as events.
        }
    }

    private async Task InspectAsync(int generation, CancellationToken ct)
    {
        DetailEvent result;
        try
        {
            var inspection = await _api.InspectContainerAsync(State.ContainerId, ct).ConfigureAwait(false);
            result = new InspectLoaded(generation, inspection);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (EngineException ex) when (ex.StatusCode == 404)
        {
            result = new InspectFailed(generation, ex.Message, NotFound: true);
        }
        catch (Exception ex)
        {
            Report(InspectFailedReport, ex);
            result = new InspectFailed(generation, ex.Message, NotFound: false);
        }

        Post(result);
    }

    private async Task FollowLogsAsync(FollowLogsEffect logs, CancellationToken ct)
    {
        var batch = new LogBatch(this, logs.Generation);
        string? error = null;
        try
        {
            await foreach (var line in _api.StreamLogsAsync(State.ContainerId, logs.Tty, logs.Request, ct).ConfigureAwait(false))
                batch.Add(line);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            batch.Discard();
            return;
        }
        catch (Exception ex)
        {
            Report(LogsFailedReport, ex);
            error = ex.Message;
        }

        // The lines read so far are shown before the end, without waiting for the tick.
        batch.Flush();
        Post(new LogStreamEnded(logs.Generation, error));
    }

    private async Task FollowStatsAsync(int generation, CancellationToken ct)
    {
        string? error = null;
        try
        {
            await foreach (var sample in _api.StreamStatsAsync(State.ContainerId, ct).ConfigureAwait(false))
                Post(new StatsSampleArrived(generation, sample));
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Report(StatsFailedReport, ex);
            error = ex.Message;
        }

        Post(new StatsStreamEnded(generation, error));
    }

    /// <summary>
    /// The log lines of one stream waiting to be applied. The first line after a batch starts a <see cref="LogTick"/> timer;
    /// when it fires, every line collected meanwhile goes to the loop as one event.
    /// </summary>
    private sealed class LogBatch(ContainerDetailSession session, int generation)
    {
        private readonly Lock _lock = new();
        private List<LogLine> _pending = [];
        private ITimer? _timer;
        private bool _closed;

        public void Add(LogLine line)
        {
            int count;
            lock (_lock)
            {
                if (_closed)
                    return;
                _pending.Add(line);
                count = _pending.Count;
                _timer ??= session._time.CreateTimer(_ => Send(), state: null, LogTick, Timeout.InfiniteTimeSpan);
            }

            session.LinesCollected?.Invoke(count);
        }

        /// <summary>Sends what is waiting now and takes no more lines: the stream ended.</summary>
        public void Flush()
        {
            lock (_lock)
            {
                _closed = true;
                SendLocked();
            }
        }

        /// <summary>Drops what is waiting and takes no more lines: the stream was stopped on purpose.</summary>
        public void Discard()
        {
            lock (_lock)
            {
                _closed = true;
                _timer?.Dispose();
                _timer = null;
                _pending = [];
            }
        }

        private void Send()
        {
            lock (_lock)
            {
                if (!_closed)
                    SendLocked();
            }
        }

        /// <summary>Posts under the lock, so a tick and the end of the stream reach the loop in the order they happened.</summary>
        private void SendLocked()
        {
            _timer?.Dispose();
            _timer = null;
            if (_pending.Count == 0)
                return;
            session.Post(new LogLinesArrived(generation, [.. _pending]));
            _pending = [];
        }
    }
}

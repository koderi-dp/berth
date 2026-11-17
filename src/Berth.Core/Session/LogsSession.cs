using System.Collections.Immutable;
using System.Threading.Channels;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Session;

/// <summary>
/// Owns the Logs page: feeds each event through <see cref="LogsReducer"/> on a single loop and carries out the effects it returns (one
/// log stream per followed container) against <see cref="IEngineApi"/>. Behaviour lives in the reducer; this class only runs it.
/// </summary>
/// <remarks>
/// Lines of every stream are collected and applied together at most every <see cref="LogTick"/>, so any number of busy containers is
/// ten state changes a second. Lines are never logged. The session uses the engine client it is given and never disposes it.
/// </remarks>
public sealed class LogsSession : IAsyncDisposable, IConnectionBound
{
    /// <summary>How long lines are collected before they are applied together.</summary>
    public static readonly TimeSpan LogTick = TimeSpan.FromMilliseconds(100);

    // What failed, as told to the error sink. Fixed wording: the exception carries the rest.
    private const string StreamFailedReport = "a container log stream of the Logs page failed";
    private const string HandlerFailedReport = "a logs state change handler failed";
    private const string LoopFailedReport = "handling a logs event failed";

    private readonly IEngineApi _api;
    private readonly TimeProvider _time;
    private readonly Action<string, Exception>? _onError;
    private readonly Channel<LogsEvent> _events = Channel.CreateUnbounded<LogsEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    private readonly LineBatch _batch;
    private LogsState _state = LogsState.Initial;
    private int _disposed;
    private int _cutOff;
    private ImmutableArray<ContainerSummary>? _lastContainers;
    private readonly Lock _releasesLock = new();
    private readonly List<Action> _releases = [];

    // Every engine call runs under _calls; once it is cancelled (stopped) no new call starts. Guarded by _callsLock.
    private readonly Lock _callsLock = new();
    private readonly CancellationTokenSource _calls = new();
    private readonly List<Task> _running = [];
    private bool _stopped;

    // Touched only by the loop, and by DisposeAsync once the loop has ended: each stream's own token, by generation.
    private readonly Dictionary<int, CancellationTokenSource> _streams = [];

    // Whether each container has a TTY (its log is plain text, not framed); read once per container. Streams run on the thread pool.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _tty = new(StringComparer.Ordinal);

    /// <param name="api">The engine client of the current connection; used, never disposed.</param>
    /// <param name="onError">
    /// Told what failed, in fixed wording, and the exception: a failed stream, a throwing <see cref="StateChanged"/> handler, or an
    /// event the loop could not handle. Never told of a cancellation the session caused. Called on a thread-pool thread.
    /// </param>
    public LogsSession(IEngineApi api, TimeProvider time, Action<string, Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(time);
        _api = api;
        _time = time;
        _onError = onError;
        _batch = new LineBatch(this);
        _loop = Task.Run(RunAsync);
    }

    /// <summary>The current state; safe to read from any thread.</summary>
    public LogsState State => Volatile.Read(ref _state);

    /// <summary>The connection this session ran on was retired: it reads nothing more, and the page opens a new one.</summary>
    public bool IsCutOff => Volatile.Read(ref _cutOff) != 0;

    /// <summary>Raised on a thread-pool thread, in order, each time the state changes.</summary>
    public event Action<LogsState>? StateChanged;

    /// <summary>Raised with the number of lines waiting for the next tick each time one is collected. For tests.</summary>
    internal event Action<int>? LinesCollected;

    /// <summary>
    /// The engine session's container list: followed when it is the first list of the connection or a new one
    /// (<paramref name="loaded"/>: before the first list, an empty one means nothing). Safe from any thread.
    /// </summary>
    public void ContainersChanged(ImmutableArray<ContainerSummary> containers, bool loaded)
    {
        if (!loaded)
            return;
        lock (_releasesLock)
        {
            if (_lastContainers is { } last && last == containers)
                return;
            _lastContainers = containers;
        }

        Post(new LogsContainersChanged(containers));
    }

    /// <summary>Cancels every stream. No <see cref="StateChanged"/> is raised after this returns.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _events.Writer.TryComplete();
        _lifetime.Cancel();
        await _loop.ConfigureAwait(false);
        Action[] releases;
        lock (_releasesLock)
        {
            releases = [.. _releases];
            _releases.Clear();
        }

        foreach (var release in releases)
            release();
        await StopCallsAsync().ConfigureAwait(false);
        _batch.Discard();

        foreach (var scope in _streams.Values)
            scope.Dispose();
        _calls.Dispose();
        _lifetime.Dispose();
    }

    /// <inheritdoc/>
    void IConnectionBound.ReleaseOnDispose(Action release)
    {
        lock (_releasesLock)
            _releases.Add(release);
    }

    /// <summary>Also called when disposed: <paramref name="release"/> runs once the session is disposed.</summary>
    internal void AlsoOnDispose(Action release) => ((IConnectionBound)this).ReleaseOnDispose(release);

    /// <inheritdoc/>
    async Task IConnectionBound.CutOffAsync()
    {
        Volatile.Write(ref _cutOff, 1);
        await StopCallsAsync().ConfigureAwait(false);
        _batch.Discard();
        Post(new LogsConnectionLost());
    }

    private void Post(LogsEvent e) => _events.Writer.TryWrite(e);

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

    private void Handle(LogsEvent e)
    {
        // An ended stream's token is no longer needed.
        if (e is LogsStreamEnded end && _streams.Remove(end.Generation, out var spent))
            spent.Dispose();

        var (next, effects) = LogsReducer.Reduce(_state, e);
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

    private void Raise(LogsState state)
    {
        if (StateChanged is not { } handlers)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<LogsState>>())
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

    private void Start(LogsEffect effect)
    {
        switch (effect)
        {
            case FollowContainerLogEffect follow:
            {
                CancellationTokenSource scope;
                lock (_callsLock)
                {
                    if (_stopped)
                        return;
                    scope = CancellationTokenSource.CreateLinkedTokenSource(_calls.Token);
                }

                _streams[follow.Generation] = scope;
                Run(ct => FollowAsync(follow, ct), scope.Token);
                break;
            }

            case StopContainerLogEffect stop when _streams.Remove(stop.Generation, out var scope):
                scope.Cancel();
                scope.Dispose();
                break;
        }
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

    private async Task FollowAsync(FollowContainerLogEffect follow, CancellationToken ct)
    {
        string? error = null;
        try
        {
            if (!_tty.TryGetValue(follow.ContainerId, out var tty))
            {
                var inspection = await _api.InspectContainerAsync(follow.ContainerId, ct).ConfigureAwait(false);
                tty = inspection.Details.Tty;
                _tty[follow.ContainerId] = tty;
            }

            await foreach (var line in _api.StreamLogsAsync(follow.ContainerId, tty, follow.Request, ct).ConfigureAwait(false))
                _batch.Add(new IncomingLogLine(follow.ContainerId, follow.Generation, line));
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (EngineException ex) when (ex.StatusCode == 404)
        {
            // The container was removed: its stream simply ended.
        }
        catch (Exception ex)
        {
            Report(StreamFailedReport, ex);
            error = ex.Message;
        }

        // The lines read so far are shown before the end, without waiting for the tick.
        _batch.Flush();
        Post(new LogsStreamEnded(follow.ContainerId, follow.Generation, error));
    }

    /// <summary>
    /// The lines of every stream waiting to be applied. The first line after a batch starts a <see cref="LogTick"/> timer; when it fires,
    /// every line collected meanwhile goes to the loop as one event.
    /// </summary>
    private sealed class LineBatch(LogsSession session)
    {
        private readonly Lock _lock = new();
        private List<IncomingLogLine> _pending = [];
        private ITimer? _timer;
        private bool _closed;

        public void Add(IncomingLogLine line)
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

        /// <summary>Sends what is waiting now (a stream ended), so its lines reach the loop before its end.</summary>
        public void Flush()
        {
            lock (_lock)
            {
                if (!_closed)
                    SendLocked();
            }
        }

        /// <summary>Drops what is waiting and takes no more lines: the session stopped.</summary>
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

        /// <summary>Posts under the lock, so a tick and a stream's end reach the loop in the order they happened.</summary>
        private void SendLocked()
        {
            _timer?.Dispose();
            _timer = null;
            if (_pending.Count == 0)
                return;
            session.Post(new LogsLinesArrived([.. _pending], session._time.GetUtcNow()));
            _pending = [];
        }
    }
}

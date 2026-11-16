using System.Collections.Immutable;
using System.Threading.Channels;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Session;

/// <summary>
/// Owns one engine session: feeds each event through <see cref="SessionReducer"/> on a single loop and carries out the
/// effects it returns against <see cref="IEngineApi"/> and the clock. Behaviour lives in the reducer; this class only
/// runs it.
/// </summary>
/// <remarks>
/// Every connection has its own <see cref="Connection"/> scope. Results, stream messages and timers are tagged with the
/// scope that started them, and the loop drops anything whose scope is no longer current, so a superseded connect or a
/// stream stopped on purpose never reaches the reducer.
/// </remarks>
public sealed class EngineSession : IAsyncDisposable
{
    private const string EngineClosedConnection = "The engine closed the connection.";

    // What failed, as told to the error sink. Fixed wording: the exception carries the rest.
    private const string ConnectFailedReport = "connecting to the engine failed";
    private const string LoadFailedReport = "loading the containers failed";
    private const string StreamFailedReport = "the engine event stream failed";
    private const string ActionFailedReport = "a container action failed";
    private const string StatsFailedReport = "a container stats stream failed";
    private const string RunCommandFailedReport = "inspecting a container for its run command failed";
    private const string HandlerFailedReport = "a state change handler failed";
    private const string LoopFailedReport = "handling a session event failed";

    /// <summary>How often the latest list stats go to the reducer, as one <see cref="StatsBatchArrived"/>.</summary>
    public static readonly TimeSpan StatsBatchInterval = TimeSpan.FromSeconds(1);

    private readonly Func<IEngineApi> _createApi;
    private readonly TimeProvider _time;
    private readonly Action<string, Exception>? _onError;
    private readonly Channel<Envelope> _events = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    private EngineState _state = EngineState.Initial;
    private int _disposed;

    // Touched only by the loop, and by DisposeAsync once the loop has ended.
    private Connection? _connection;
    private ITimer? _retryTimer;
    private object? _retryKey;
    private ITimer? _reloadTimer;
    private StatsWatch? _stats;
    private readonly List<Task> _retiring = [];

    // The current connection once connected, else null. Written by the loop, read by OpenDetail.
    private Connection? _connected;

    public EngineSession(Func<IEngineApi> createApi, TimeProvider time)
        : this(createApi, time, onError: null)
    {
    }

    /// <param name="onError">
    /// Told what failed, in fixed wording, and the exception: a failed connect, list load, event stream or action, a throwing
    /// <see cref="StateChanged"/> handler, or an event the loop could not handle. Never told of a cancellation the session caused.
    /// Called on a thread-pool thread; an exception it throws is ignored.
    /// </param>
    public EngineSession(Func<IEngineApi> createApi, TimeProvider time, Action<string, Exception>? onError)
    {
        ArgumentNullException.ThrowIfNull(createApi);
        ArgumentNullException.ThrowIfNull(time);
        _createApi = createApi;
        _time = time;
        _onError = onError;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>The current state; safe to read from any thread.</summary>
    public EngineState State => Volatile.Read(ref _state);

    /// <summary>
    /// Raised on a thread-pool thread, in order, each time the state changes. The effects of the new state are already
    /// under way when it is raised.
    /// </summary>
    public event Action<EngineState>? StateChanged;

    /// <summary>
    /// Raised on the loop with text the user asked to copy (a <c>docker run</c> command read from the engine), for the app to put on
    /// the clipboard. The text may hold environment values: never log it.
    /// </summary>
    public event Action<string>? CopyRequested;

    /// <summary>Raised on the loop when an event from an older connection or timer is dropped. For tests.</summary>
    internal event Action<SessionEvent>? StaleEventDropped;

    /// <summary>Raised on a stream's thread each time a list stats sample of the container with this id is read. For tests.</summary>
    internal event Action<string>? StatsSampleRecorded;

    /// <summary>
    /// A detail page's session for <paramref name="containerId"/> on the current connection, not opened yet (call
    /// <see cref="ContainerDetailSession.Open"/>), or null while not connected. It shares this session's engine client and
    /// clock, reports to the same error sink, and is cut off (its calls stopped, <see cref="DetailState.ConnectionLost"/> set)
    /// when that connection is retired, before the client is disposed. The caller disposes it. Safe from any thread.
    /// </summary>
    public ContainerDetailSession? OpenDetail(string containerId)
    {
        ArgumentNullException.ThrowIfNull(containerId);
        return Volatile.Read(ref _connected)?.Attach(api => new ContainerDetailSession(api, containerId, _time, _onError));
    }

    /// <summary>Queues <paramref name="e"/> for the loop. Safe from any thread; never throws; ignored after disposal.</summary>
    public void Dispatch(SessionEvent e) => Post(e, source: null);

    /// <summary>Cancels streams, timers and calls. No <see cref="StateChanged"/> is raised after this returns.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _events.Writer.TryComplete();
        _lifetime.Cancel();
        await _loop.ConfigureAwait(false);

        _retryTimer?.Dispose();
        Retire();
        await Task.WhenAll(_retiring).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private void Post(SessionEvent e, object? source) => _events.Writer.TryWrite(new Envelope(e, source));

    private async Task RunAsync()
    {
        try
        {
            await foreach (var (e, source) in _events.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (source is not null && !ReferenceEquals(source, _connection) && !ReferenceEquals(source, _retryKey))
                {
                    Raise(StaleEventDropped, e);
                    continue;
                }

                try
                {
                    Handle(e);
                }
                catch (Exception ex)
                {
                    // A throwing reducer or effect start must not end the loop: the UI would freeze on its last state.
                    Report(LoopFailedReport, ex);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    /// <summary>Runs one event through the reducer, starts its effects, then tells observers of the new state.</summary>
    private void Handle(SessionEvent e)
    {
        var (next, effects) = SessionReducer.Reduce(_state, e);
        var changed = !ReferenceEquals(next, _state);
        Volatile.Write(ref _state, next);

        try
        {
            // Effects start before observers hear of the state, so a timer the state implies already exists.
            foreach (var effect in effects)
                Start(effect);
        }
        finally
        {
            Volatile.Write(ref _connected, next.Status == ConnectionStatus.Connected ? _connection : null);
            if (changed)
                Raise(StateChanged, next);
        }
    }

    /// <summary>
    /// Invokes each handler, reporting and otherwise ignoring exceptions: a throwing observer must not stop the loop or keep
    /// later observers from hearing of the change.
    /// </summary>
    private void Raise<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(value);
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

    private void Start(SessionEffect effect)
    {
        switch (effect)
        {
            case ConnectEffect:
                Retire();
                _connection = new Connection();
                StartConnect(_connection);
                break;

            case StopStreamsEffect:
                Retire();
                break;

            case LoadContainersEffect load when _connection is { } connection:
                connection.Run(ct => LoadAsync(connection, load.Generation, ct));
                break;

            case StartEventStreamEffect when _connection is { } connection:
                connection.Run(ct => FollowEventsAsync(connection, ct));
                break;

            case RunActionEffect action when _connection is { } connection:
                connection.Run(ct => RunActionAsync(connection, action, ct));
                break;

            case InspectForRunCommandEffect inspect when _connection is { } connection:
                connection.Run(ct => InspectForRunCommandAsync(connection, inspect, ct));
                break;

            case CopyTextEffect copy:
                Raise(CopyRequested, copy.Text);
                break;

            case ScheduleRetryEffect retry:
            {
                _retryTimer?.Dispose();
                var key = new object();
                _retryKey = key;
                _retryTimer = StartTimer(retry.Delay, new RetryDue(), key);
                break;
            }

            case WatchStatsEffect watch when _connection is { } connection:
                _stats ??= new StatsWatch(this, connection);
                _stats.Watch(watch.ContainerIds);
                break;

            case ScheduleReloadEffect reload:
                _reloadTimer?.Dispose();
                _reloadTimer = StartTimer(reload.Delay, new ReloadDue(), _connection);
                break;
        }
    }

    private ITimer StartTimer(TimeSpan delay, SessionEvent due, object? source) =>
        _time.CreateTimer(_ => Post(due, source), state: null, delay, Timeout.InfiniteTimeSpan);

    /// <summary>Stops everything belonging to the current connection; its late results are dropped by the loop.</summary>
    private void Retire()
    {
        _reloadTimer?.Dispose();
        _reloadTimer = null;
        _stats?.Dispose();
        _stats = null;
        if (_connection is null)
            return;

        _retiring.RemoveAll(t => t.IsCompleted);
        _retiring.Add(_connection.RetireAsync());
        _connection = null;
    }

    private void StartConnect(Connection connection) => connection.Run(async ct =>
    {
        SessionEvent result;
        try
        {
            var api = _createApi();
            connection.Api = api;
            var version = await api.ConnectAsync(ct).ConfigureAwait(false);
            var info = await api.GetInfoAsync(ct).ConfigureAwait(false);
            result = new ConnectSucceeded(version, info);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Report(ConnectFailedReport, ex);
            result = new ConnectFailed(ex.Message);
        }

        Post(result, connection);
    });

    private async Task LoadAsync(Connection connection, int generation, CancellationToken ct)
    {
        SessionEvent result;
        try
        {
            var api = connection.Api!;
            var containers = await api.ListContainersAsync(ct).ConfigureAwait(false);
            var info = await api.GetInfoAsync(ct).ConfigureAwait(false);
            result = new ContainersLoaded(generation, [.. containers], info);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Report(LoadFailedReport, ex);
            result = new LoadFailed(generation, ex.Message, IsUnreachable(ex));
        }

        Post(result, connection);
    }

    private async Task FollowEventsAsync(Connection connection, CancellationToken ct)
    {
        string message;
        try
        {
            await foreach (var received in connection.Api!.StreamEventsAsync(ct).ConfigureAwait(false))
                Post(new EngineEventReceived(received), connection);
            message = EngineClosedConnection;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Report(StreamFailedReport, ex);
            message = ex.Message;
        }

        Post(new ConnectionLost(message), connection);
    }

    private async Task RunActionAsync(Connection connection, RunActionEffect action, CancellationToken ct)
    {
        SessionEvent result;
        try
        {
            var api = connection.Api!;
            var call = action.Action switch
            {
                ContainerAction.Start => api.StartContainerAsync(action.Id, ct),
                ContainerAction.Stop => api.StopContainerAsync(action.Id, ct),
                ContainerAction.Restart => api.RestartContainerAsync(action.Id, ct),
                ContainerAction.Pause => api.PauseContainerAsync(action.Id, ct),
                ContainerAction.Unpause => api.UnpauseContainerAsync(action.Id, ct),
                ContainerAction.Remove => api.RemoveContainerAsync(action.Id, action.Force, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(action), action.Action, "Unknown container action."),
            };
            await call.ConfigureAwait(false);
            result = new ActionSucceeded(action.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Report(ActionFailedReport, ex);
            result = new ActionFailed(action.Id, ex.Message, IsUnreachable(ex));
        }

        Post(result, connection);
    }

    private async Task InspectForRunCommandAsync(Connection connection, InspectForRunCommandEffect inspect, CancellationToken ct)
    {
        SessionEvent result;
        try
        {
            var inspection = await connection.Api!.InspectContainerAsync(inspect.Id, ct).ConfigureAwait(false);
            result = new RunCommandInspected(inspect.Id, inspect.Shell, inspection.Details);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Report(RunCommandFailedReport, ex);
            result = new RunCommandFailed(inspect.Id, ex.Message, IsUnreachable(ex));
        }

        Post(result, connection);
    }

    /// <summary>
    /// Follows one container's stats for the list until cancelled or the stream ends. A stream that ends (the container
    /// stopped) or finds the container gone is not a failure; any other failure is reported and leaves the other streams be.
    /// </summary>
    private async Task FollowStatsAsync(Connection connection, StatsWatch watch, string id, CancellationToken ct)
    {
        try
        {
            await foreach (var sample in connection.Api!.StreamStatsAsync(id, ct).ConfigureAwait(false))
            {
                watch.Record(id, sample, ct);
                Raise(StatsSampleRecorded, id);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (EngineException ex) when (ex.StatusCode == 404)
        {
        }
        catch (Exception ex)
        {
            Report(StatsFailedReport, ex);
        }
    }

    private static bool IsUnreachable(Exception ex) => ex is EngineException { StatusCode: 0 };

    private readonly record struct Envelope(SessionEvent Event, object? Source);

    /// <summary>
    /// One connection attempt and everything started on it: the engine client, its calls, its stream and the detail sessions
    /// opened on it.
    /// </summary>
    private sealed class Connection
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _calls = [];
        private readonly Lock _detailsLock = new();
        private readonly HashSet<ContainerDetailSession> _details = [];
        private bool _retired;

        /// <summary>Set by the connect call before it reports success, so every later call can use it.</summary>
        public IEngineApi? Api { get; set; }

        /// <summary>Cancelled when the connection is retired. Called only from the loop.</summary>
        public CancellationToken Token => _stop.Token;

        /// <summary>Runs <paramref name="call"/> on the thread pool. Called only from the loop.</summary>
        public Task Run(Func<CancellationToken, Task> call)
        {
            _calls.RemoveAll(t => t.IsCompleted);
            var token = _stop.Token;
            var task = Task.Run(() => call(token));
            _calls.Add(task);
            return task;
        }

        /// <summary>
        /// Creates a detail session on this connection's engine client and tracks it until the page disposes it, or returns
        /// null when the client is not set or the connection is retired. Safe from any thread.
        /// </summary>
        public ContainerDetailSession? Attach(Func<IEngineApi, ContainerDetailSession> create)
        {
            lock (_detailsLock)
            {
                if (_retired || Api is not { } api)
                    return null;

                var detail = create(api);
                _details.Add(detail);
                detail.ReleaseOnDispose(() =>
                {
                    lock (_detailsLock)
                        _details.Remove(detail);
                });
                return detail;
            }
        }

        /// <summary>
        /// Cancels every call and cuts off the detail sessions, waits for all of them to finish, then disposes the engine
        /// client.
        /// </summary>
        public async Task RetireAsync()
        {
            ContainerDetailSession[] details;
            lock (_detailsLock)
            {
                _retired = true;
                details = [.. _details];
                _details.Clear();
            }

            var calls = _calls.ToArray();
            try
            {
                _stop.Cancel();
                await Task.WhenAll(calls.Concat(details.Select(d => d.CutOffAsync()))).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Calls report their own failures as events; a throwing cancellation callback has no one to tell.
            }

            if (Api is { } api)
            {
                try
                {
                    await api.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The connection is already abandoned; a failing dispose changes nothing the user can act on.
                }
            }

            _stop.Dispose();
        }
    }

    /// <summary>
    /// The list stats of one connection: a stream per watched container and a timer that sends the latest sample of each,
    /// once a <see cref="StatsBatchInterval"/>, as one event. Everything but <see cref="Record"/> and the timer is called only
    /// from the loop.
    /// </summary>
    private sealed class StatsWatch(EngineSession session, Connection connection) : IDisposable
    {
        private readonly Dictionary<string, (CancellationTokenSource Stop, Task Stream)> _streams = new(StringComparer.Ordinal);
        private readonly Lock _lock = new();
        private Dictionary<string, StatsSample> _fresh = new(StringComparer.Ordinal);
        private ITimer? _tick;

        /// <summary>
        /// Follows exactly <paramref name="ids"/>: stops the streams of ids left out and starts those of new ids, or of ids
        /// whose stream has ended (the container was started again); the others carry on.
        /// </summary>
        public void Watch(ImmutableArray<string> ids)
        {
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            foreach (var id in _streams.Keys.Where(id => !wanted.Contains(id)).ToList())
                Stop(id);

            foreach (var id in ids)
            {
                if (_streams.TryGetValue(id, out var current))
                {
                    if (!current.Stream.IsCompleted)
                        continue;
                    Stop(id);
                }

                Start(id);
            }

            if (_streams.Count == 0)
            {
                _tick?.Dispose();
                _tick = null;
            }
            else
            {
                _tick ??= session._time.CreateTimer(_ => Send(), state: null, StatsBatchInterval, StatsBatchInterval);
            }
        }

        /// <summary>Keeps <paramref name="sample"/> for the next batch unless its stream was stopped. Called from the stream.</summary>
        public void Record(string id, StatsSample sample, CancellationToken ct)
        {
            lock (_lock)
            {
                if (!ct.IsCancellationRequested)
                    _fresh[id] = sample;
            }
        }

        /// <summary>Stops the timer and every stream.</summary>
        public void Dispose()
        {
            _tick?.Dispose();
            _tick = null;
            foreach (var id in _streams.Keys.ToList())
                Stop(id);
        }

        private void Start(string id)
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            var token = stop.Token;
            var stream = connection.Run(_ => session.FollowStatsAsync(connection, this, id, token));
            _streams[id] = (stop, stream);
        }

        private void Stop(string id)
        {
            var (stop, _) = _streams[id];
            _streams.Remove(id);
            stop.Cancel();
            stop.Dispose();
            lock (_lock)
                _fresh.Remove(id);
        }

        /// <summary>Sends the samples read since the last batch, if any. Called by the timer.</summary>
        private void Send()
        {
            ImmutableDictionary<string, StatsSample> batch;
            lock (_lock)
            {
                if (_fresh.Count == 0)
                    return;
                batch = _fresh.ToImmutableDictionary(StringComparer.Ordinal);
                _fresh = new Dictionary<string, StatsSample>(StringComparer.Ordinal);
            }

            session.Post(new StatsBatchArrived(batch), connection);
        }
    }
}

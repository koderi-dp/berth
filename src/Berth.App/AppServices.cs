using Berth.Connections;
using Berth.Session;
using Berth.Settings;

namespace Berth;

/// <summary>
/// What the app owns besides its window: the settings, the engine session for the active connection and the detail session of the
/// container whose page is open. Created once by the entry
/// point (<see cref="Create"/>); the components reach it through <see cref="Current"/>. Changes are made on the UI thread; the events
/// are raised on the thread that made the change.
/// </summary>
internal sealed class AppServices
{
    private static AppServices? _current;

    private readonly SettingsStore _store;
    private AppSettings _settings;
    private EngineSession _session;
    private ContainerDetailSession? _detail;
    private int _requests;

    private AppServices(SettingsStore store)
    {
        _store = store;
        var load = store.Load();
        _settings = load.Settings;
        SettingsWereSetAside = load.SetAside;
        if (load.SetAside)
            AppLog.Write("settings file was damaged and set aside");
        if (load.SaveError is { } saveError)
            AppLog.Error("saving the first-run settings failed", saveError);
        _session = CreateSession(ActiveConnection(_settings));
    }

    public static AppServices Current => _current ?? throw new InvalidOperationException("AppServices.Create has not been called.");

    /// <summary>Loads the settings from <paramref name="directory"/> and creates the session for the active connection, not yet connecting.</summary>
    /// <param name="dockerHost">The value of <c>DOCKER_HOST</c>, used for the first-run connection.</param>
    public static AppServices Create(string directory, string? dockerHost)
    {
        if (_current is not null)
            throw new InvalidOperationException("AppServices has already been created.");
        _current = new AppServices(new SettingsStore(directory, dockerHost));
        return _current;
    }

    public AppSettings Settings => Volatile.Read(ref _settings);

    /// <summary>The connection the session runs on (the first one when the settings name none).</summary>
    public ConnectionProfile ActiveConnectionProfile => ActiveConnection(Settings);

    /// <summary>The settings file was damaged at start: it was kept as <c>settings.bad.json</c> and Berth started with defaults.</summary>
    public bool SettingsWereSetAside { get; }

    public event Action? SettingsChanged;

    /// <summary>The session for the active connection. Replaced, and <see cref="SessionChanged"/> raised, when that connection changes.</summary>
    public EngineSession Session => Volatile.Read(ref _session);

    public event Action? SessionChanged;

    /// <summary>Which session <see cref="Session"/> is: 0 for the first, one more for each that replaced it.</summary>
    public int SessionNumber { get; private set; }

    /// <summary>
    /// The detail session of the container whose page is open, on the current connection; null while none is. Replaced, and
    /// <see cref="DetailChanged"/> raised, by <see cref="OpenDetail"/> and <see cref="CloseDetail"/>; closed when the connection is switched.
    /// </summary>
    public ContainerDetailSession? Detail => Volatile.Read(ref _detail);

    public event Action? DetailChanged;

    /// <summary>A number for a dialog's request to the session (a volume create, a run), unique for this run of the app.</summary>
    public int NextRequestId() => Interlocked.Increment(ref _requests);

    /// <summary>
    /// Opens a detail session for <paramref name="containerId"/> on the current connection and starts reading it, in place of the one
    /// open (which is disposed). While not connected none can open: the one open is closed and false is returned.
    /// </summary>
    public bool OpenDetail(string containerId)
    {
        var next = Session.OpenDetail(containerId);
        next?.Open();
        ReplaceDetail(next);
        return next is not null;
    }

    /// <summary>Closes the open detail session, if any: the page left, or its connection was lost.</summary>
    public void CloseDetail() => ReplaceDetail(null);

    /// <summary>
    /// The app is exiting: closes the open detail session, so a shell it holds is ended in the container, waiting at most
    /// <paramref name="limit"/> (on the calling thread) before the exit goes on without it.
    /// </summary>
    public void CloseDetailForExit(TimeSpan limit)
    {
        if (Interlocked.Exchange(ref _detail, null) is not { } detail)
            return;
        Task.Run(() => DisposeAsync(detail)).Wait(limit);
    }

    /// <summary>
    /// Applies <paramref name="change"/> and saves the result. When the active connection, or any field of it, is different afterwards,
    /// the session is replaced by one for the new connection, which starts connecting. A save that fails is logged and the change is
    /// kept for this run.
    /// </summary>
    /// <returns>False when the save failed (the change applies until Berth closes); true when it was saved or changed nothing.</returns>
    public bool UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        var old = Settings;
        var next = change(old);
        if (next.Connections.Count == 0)
            throw new ArgumentException("Settings need at least one connection.", nameof(change));
        if (next == old)
            return true;

        var saved = true;
        try
        {
            _store.Save(next);
        }
        catch (Exception ex)
        {
            AppLog.Error("saving the settings failed", ex);
            saved = false;
        }

        Volatile.Write(ref _settings, next);
        SettingsChanged?.Invoke();

        var connection = ActiveConnection(next);
        var previous = ActiveConnection(old);
        if (connection != previous)
            ReplaceSession(connection, previous);
        return saved;
    }

    /// <summary>Makes the connection <paramref name="id"/> the active one. An unknown id, or the one already active, changes nothing.</summary>
    public void SwitchConnection(string id)
    {
        var settings = Settings;
        if (id == settings.ActiveConnectionId || !settings.Connections.Any(connection => connection.Id == id))
            return;

        UpdateSettings(current => current with { ActiveConnectionId = id });
    }

    /// <summary>The active connection; the first one when the id names none (the settings page keeps it valid, a hand edit may not).</summary>
    private static ConnectionProfile ActiveConnection(AppSettings settings) =>
        settings.Connections.FirstOrDefault(connection => connection.Id == settings.ActiveConnectionId) ?? settings.Connections[0];

    /// <summary>
    /// A session for <paramref name="connection"/> whose failures go to the log (fixed wording and exception type names only), whose
    /// text to copy (a <c>docker run</c> command read for the list) goes to the clipboard, and which runs the connection's engine control
    /// commands when it has them.
    /// </summary>
    private EngineSession CreateSession(ConnectionProfile connection)
    {
        var engineControl = connection.EngineControl is { } control ? new EngineCommands(control) : null;
        var session = new EngineSession(() => EngineConnection.Open(connection), TimeProvider.System, AppLog.Error, engineControl);
        session.CopyRequested += text => CopyFromSession(session, text);
        return session;
    }

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard on the UI thread, unless <paramref name="session"/> was replaced in the meantime (the
    /// user switched to another engine). The text is never logged: it may hold environment values. A failure shows in the error bar.
    /// </summary>
    private void CopyFromSession(EngineSession session, string text) =>
        Microsoft.UI.Reactor.ReactorApp.UIDispatcher?.TryEnqueue(() =>
        {
            if (ReferenceEquals(session, Session))
                Components.ContainerParts.CopyToClipboard(text);
        });

    private void ReplaceDetail(ContainerDetailSession? next)
    {
        var old = Interlocked.Exchange(ref _detail, next);
        if (old is null && next is null)
            return;
        DetailChanged?.Invoke();
        if (old is not null)
            _ = Task.Run(() => DisposeAsync(old));
    }

    /// <summary>
    /// Replaces the session by one for <paramref name="connection"/>, which connects at once; except when it is
    /// <paramref name="previous"/> edited without a change of address and with engine control still on, and Berth had stopped its
    /// engine: the stop holds (<see cref="EngineStoppedCarriedOver"/>) and nothing connects until Start engine or Retry. An engine
    /// command still running in the old session runs to its end; its result is dropped with that session.
    /// </summary>
    private void ReplaceSession(ConnectionProfile connection, ConnectionProfile previous)
    {
        var stoppedByBerth = Session.State.EngineControl.StoppedByBerth;
        var sameEngine = previous.Id == connection.Id && previous with { Name = connection.Name, EngineControl = connection.EngineControl } == connection;

        // The detail session runs on the old connection; the page goes back to the list (App follows SessionChanged).
        CloseDetail();
        var next = CreateSession(connection);
        var old = Interlocked.Exchange(ref _session, next);
        SessionNumber++;
        AppLog.Write("connection switched");
        SessionChanged?.Invoke();
        if (sameEngine && stoppedByBerth && connection.EngineControl is not null)
            next.Dispatch(new EngineStoppedCarriedOver());
        else
            next.Dispatch(new ConnectRequested());
        _ = Task.Run(() => DisposeAsync(old));
    }

    /// <summary>
    /// Whether the connection <paramref name="id"/> is the active one and its engine command runs now: an edit of it would replace
    /// its session, so the editor waits.
    /// </summary>
    public bool EngineCommandRunsFor(string? id) =>
        id is not null && id == ActiveConnectionProfile.Id && Session.State.EngineControl.Running is not null;

    private static async Task DisposeAsync(IAsyncDisposable session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error(session is ContainerDetailSession ? "closing a detail session failed" : "closing the previous session failed", ex);
        }
    }
}

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

    /// <summary>The settings file was damaged at start: it was kept as <c>settings.bad.json</c> and Berth started with defaults.</summary>
    public bool SettingsWereSetAside { get; }

    public event Action? SettingsChanged;

    /// <summary>The session for the active connection. Replaced, and <see cref="SessionChanged"/> raised, when that connection changes.</summary>
    public EngineSession Session => Volatile.Read(ref _session);

    public event Action? SessionChanged;

    /// <summary>
    /// The detail session of the container whose page is open, on the current connection; null while none is. Replaced, and
    /// <see cref="DetailChanged"/> raised, by <see cref="OpenDetail"/> and <see cref="CloseDetail"/>; closed when the connection is switched.
    /// </summary>
    public ContainerDetailSession? Detail => Volatile.Read(ref _detail);

    public event Action? DetailChanged;

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
        if (connection != ActiveConnection(old))
            ReplaceSession(connection);
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

    /// <summary>A session for <paramref name="connection"/> whose failures go to the log (fixed wording and exception type names only).</summary>
    private static EngineSession CreateSession(ConnectionProfile connection) =>
        new(() => EngineConnection.Open(connection), TimeProvider.System, AppLog.Error);

    private void ReplaceDetail(ContainerDetailSession? next)
    {
        var old = Interlocked.Exchange(ref _detail, next);
        if (old is null && next is null)
            return;
        DetailChanged?.Invoke();
        if (old is not null)
            _ = Task.Run(() => DisposeAsync(old));
    }

    private void ReplaceSession(ConnectionProfile connection)
    {
        // The detail session runs on the old connection; the page goes back to the list (App follows SessionChanged).
        CloseDetail();
        var next = CreateSession(connection);
        var old = Interlocked.Exchange(ref _session, next);
        AppLog.Write("connection switched");
        SessionChanged?.Invoke();
        next.Dispatch(new ConnectRequested());
        _ = Task.Run(() => DisposeAsync(old));
    }

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

using Berth.Connections;
using Berth.Session;
using Berth.Settings;

namespace Berth;

/// <summary>
/// What the app owns besides its window: the settings and the engine session for the active connection. Created once by the entry
/// point (<see cref="Create"/>); the components reach it through <see cref="Current"/>. Changes are made on the UI thread; the events
/// are raised on the thread that made the change.
/// </summary>
internal sealed class AppServices
{
    private static AppServices? _current;

    private readonly SettingsStore _store;
    private AppSettings _settings;
    private EngineSession _session;

    private AppServices(SettingsStore store)
    {
        _store = store;
        _settings = store.Load();
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

    public event Action? SettingsChanged;

    /// <summary>The session for the active connection. Replaced, and <see cref="SessionChanged"/> raised, when that connection changes.</summary>
    public EngineSession Session => Volatile.Read(ref _session);

    public event Action? SessionChanged;

    /// <summary>
    /// Applies <paramref name="change"/> and saves the result. When the active connection, or any field of it, is different afterwards,
    /// the session is replaced by one for the new connection, which starts connecting. A save that fails is logged and the change is
    /// kept for this run.
    /// </summary>
    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        var old = Settings;
        var next = change(old);
        if (next.Connections.Count == 0)
            throw new ArgumentException("Settings need at least one connection.", nameof(change));
        if (next == old)
            return;

        try
        {
            _store.Save(next);
        }
        catch (Exception ex)
        {
            AppLog.Error("saving the settings failed", ex);
        }

        Volatile.Write(ref _settings, next);
        SettingsChanged?.Invoke();

        var connection = ActiveConnection(next);
        if (connection != ActiveConnection(old))
            ReplaceSession(connection);
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

    private static EngineSession CreateSession(ConnectionProfile connection) =>
        new(() => EngineConnection.Open(connection), TimeProvider.System);

    private void ReplaceSession(ConnectionProfile connection)
    {
        var next = CreateSession(connection);
        var old = Interlocked.Exchange(ref _session, next);
        AppLog.Write("connection switched");
        SessionChanged?.Invoke();
        next.Dispatch(new ConnectRequested());
        _ = Task.Run(() => DisposeAsync(old));
    }

    private static async Task DisposeAsync(EngineSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("closing the previous session failed", ex);
        }
    }
}

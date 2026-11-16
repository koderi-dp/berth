using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Shell;

/// <summary>
/// The notification area icon, through Reactor's own tray icon (<see cref="ReactorApp.OpenTrayIcon"/>, Shell_NotifyIcon underneath).
/// Its icon and tooltip follow the engine state of the active connection; left click shows the window, right click shows the menu
/// (<see cref="TrayFlyout"/>) built from <see cref="Menu"/>. It follows the session directly, not through the hooks the pages use, so
/// it stays right while the window is hidden and the pages do not render. Container actions asked from the menu go through the
/// session like the list's; when one fails, or an engine command fails while the window is hidden, a notification gives the message.
/// UI thread only, except where noted.
/// </summary>
internal static class TrayIcon
{
    private static ReactorTrayIcon? _icon;
    private static TrayMenu? _menu;
    private static EngineSession? _session;
    private static int _updatePending;
    private static string? _lastActionError;
    private static EngineCommandFailure? _lastFailure;

    // Containers whose Stop or Restart was asked from the tray, and whether the session has shown the action as under way yet.
    private static readonly Dictionary<string, bool> Asked = new(StringComparer.Ordinal);

    /// <summary>Raised on the UI thread when <see cref="Menu"/> changes.</summary>
    public static event Action? MenuChanged;

    /// <summary>Whether the icon is in the notification area. Read from any thread.</summary>
    public static bool IsOpen => Volatile.Read(ref _icon) is not null;

    /// <summary>The menu as the state gives it now; a new instance only when it differs from the last one.</summary>
    public static TrayMenu Menu => _menu ?? Build();

    /// <summary>Puts the icon in the notification area and starts following the session. Logs and does nothing when it cannot.</summary>
    public static void Open()
    {
        if (_icon is not null)
            return;

        _menu = Build();
        ReactorTrayIcon? icon;
        try
        {
            icon = ReactorApp.OpenTrayIcon(new TrayIconSpec(TrayIcons.For(_menu.Icon), _menu.Tooltip));
        }
        catch (Exception ex)
        {
            AppLog.Error("opening the tray icon failed", ex);
            return;
        }

        if (icon is null)
        {
            AppLog.Write("the tray icon could not be opened");
            return;
        }

        icon.Click += (_, _) => MainWindow.Show();
        icon.RightClick += (_, _) => ShowMenu();
        Volatile.Write(ref _icon, icon);
        AppServices.Current.SessionChanged += Follow;
        AppServices.Current.SettingsChanged += Update;
        Follow();
    }

    /// <summary>Removes the icon from the notification area (no icon is left behind when Berth exits). Safe to call twice.</summary>
    public static void Close()
    {
        var icon = Interlocked.Exchange(ref _icon, null);
        if (icon is null)
            return;
        AppServices.Current.SessionChanged -= Follow;
        AppServices.Current.SettingsChanged -= Update;
        if (_session is not null)
            _session.StateChanged -= OnStateChanged;
        _session = null;
        try
        {
            icon.HideFlyout();
            icon.Close();
        }
        catch (Exception ex)
        {
            AppLog.Error("removing the tray icon failed", ex);
        }
    }

    /// <summary>Hides the menu (after a choice in it).</summary>
    public static void HideMenu() => _icon?.HideFlyout();

    /// <summary>
    /// Stops or restarts a container from the menu, through the session as the list does; nothing when <paramref name="session"/>,
    /// the one the menu was built from, was replaced since (the container was on another engine).
    /// </summary>
    public static void Act(EngineSession session, string containerId, ContainerAction action)
    {
        if (!ReferenceEquals(session, AppServices.Current.Session))
            return;
        Asked[containerId] = false;
        session.Dispatch(new ActionRequested([containerId], action));
    }

    private static void ShowMenu()
    {
        try
        {
            _icon?.ShowFlyout(Component<TrayFlyout>());
        }
        catch (Exception ex)
        {
            AppLog.Error("showing the tray menu failed", ex);
        }
    }

    private static TrayMenu Build() => TrayMenu.For(AppServices.Current.ActiveConnectionProfile.Name, AppServices.Current.Session.State);

    /// <summary>Moves to the current session (a switch of connection replaces it) and updates.</summary>
    private static void Follow()
    {
        if (_session is not null)
            _session.StateChanged -= OnStateChanged;
        _session = AppServices.Current.Session;
        _session.StateChanged += OnStateChanged;
        _lastActionError = _session.State.ActionError;
        _lastFailure = _session.State.EngineControl.Failure;
        Asked.Clear();
        Update();
    }

    /// <summary>On the session's thread: coalesced into one pending update on the UI thread.</summary>
    private static void OnStateChanged(EngineState _)
    {
        if (Interlocked.Exchange(ref _updatePending, 1) == 0 && ReactorApp.UIDispatcher?.TryEnqueue(Update) != true)
            Volatile.Write(ref _updatePending, 0);
    }

    private static void Update()
    {
        Volatile.Write(ref _updatePending, 0);
        if (_icon is not { } icon)
            return;

        var state = AppServices.Current.Session.State;
        NotifyFailures(state);

        var next = Build();
        if (next.Equals(_menu))
            return;
        var iconChanged = _menu is null || _menu.Icon != next.Icon || _menu.Tooltip != next.Tooltip;
        _menu = next;
        if (iconChanged)
            icon.Update(new TrayIconSpec(TrayIcons.For(next.Icon), next.Tooltip));
        MenuChanged?.Invoke();
    }

    /// <summary>
    /// A container action asked from the tray that ended with a new error in the error bar, or an engine command that failed while the
    /// window is hidden: a notification with the message as-is.
    /// </summary>
    private static void NotifyFailures(EngineState state)
    {
        // An action that failed ends with the error bar's new message in the same change; one that ended between two updates was never
        // seen under way, so any asked container no longer under way counts when the error is new.
        var errorIsNew = state.ActionError is not null && state.ActionError != _lastActionError;
        var ended = false;
        foreach (var (id, seen) in Asked.ToArray())
        {
            if (state.Pending.ContainsKey(id))
            {
                Asked[id] = true;
            }
            else if (seen || errorIsNew)
            {
                Asked.Remove(id);
                ended = true;
            }
        }

        if (errorIsNew && ended)
            Notifications.Show("A container action failed", state.ActionError!);
        _lastActionError = state.ActionError;

        var failure = state.EngineControl.Failure;
        if (failure is not null && !ReferenceEquals(failure, _lastFailure) && MainWindow.IsHidden)
            Notifications.Show(failure.Command == EngineCommand.Start ? "The engine did not start" : "The engine did not stop", failure.Message);
        _lastFailure = failure;
    }
}

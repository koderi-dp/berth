using Berth.Session;

namespace Berth.Hooks;

/// <summary>
/// Whether a page's store may render now: a page kept in the navigation cache while another page shows must do no rendering work, so
/// its subscription holds changes back while the gate is closed and delivers the latest when it opens. UI thread only.
/// </summary>
internal sealed class RenderGate
{
    /// <summary>The Logs page: open while it is the page shown (set by <c>App</c>).</summary>
    public static readonly RenderGate LogsPage = new();

    private bool _open;

    public bool IsOpen
    {
        get => _open;
        set
        {
            if (value == _open)
                return;
            _open = value;
            Changed?.Invoke();
        }
    }

    public event Action? Changed;
}

/// <summary>
/// The Logs page's session state as an external store for <c>UseExternalStore</c>: the snapshot is the state of
/// <see cref="AppServices.Logs"/> (null while none is open), and a subscriber hears of its changes on the UI thread, only while the
/// window shows and the Logs page is the page shown (<see cref="RenderGate.LogsPage"/>).
/// </summary>
internal static class LogsStateHook
{
    public static readonly Func<Action, Action> Subscribe = onChange =>
        UiThreadSubscription<LogsSession, LogsState>.Start(
            onChange,
            () => AppServices.Current.Logs,
            handler => AppServices.Current.LogsChanged += handler,
            handler => AppServices.Current.LogsChanged -= handler,
            (session, handler) => session.StateChanged += handler,
            (session, handler) => session.StateChanged -= handler,
            RenderGate.LogsPage);

    public static readonly Func<LogsState?> Snapshot = () => AppServices.Current.Logs?.State;

    /// <summary>The reducer returns the same instance when nothing changed.</summary>
    public static readonly IEqualityComparer<LogsState?> SameState = ReferenceEqualityComparer.Instance;
}

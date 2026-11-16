using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace Berth.Hooks;

/// <summary>
/// The open container detail session's state as an external store for <c>UseExternalStore</c>: the snapshot is the state of
/// <see cref="AppServices.Detail"/> (null while none is open), and a subscriber hears of every change of it on the UI thread
/// (<see cref="UiThreadSubscription{TSource, TState}"/>), also when the session is replaced or closed.
/// </summary>
internal static class DetailStateHook
{
    /// <summary>Subscribes <c>onChange</c> to the open detail session (and each one that replaces it); returns the unsubscribe action.</summary>
    public static readonly Func<Action, Action> Subscribe = onChange =>
        UiThreadSubscription<ContainerDetailSession, DetailState>.Start(
            onChange,
            () => AppServices.Current.Detail,
            handler => AppServices.Current.DetailChanged += handler,
            handler => AppServices.Current.DetailChanged -= handler,
            (session, handler) => session.StateChanged += handler,
            (session, handler) => session.StateChanged -= handler);

    public static readonly Func<DetailState?> Snapshot = () => AppServices.Current.Detail?.State;

    /// <summary>The reducer returns the same instance when nothing changed, so identity is the right test, and a cheap one.</summary>
    public static readonly IEqualityComparer<DetailState?> SameState = ReferenceEqualityComparer.Instance;
}

/// <summary>A component with props that shows a container's detail state and the engine state.</summary>
internal abstract class DetailStateComponent<TProps> : Component<TProps>
{
    /// <summary>The open detail session's state, or null while none is open. Re-renders on the UI thread when it changes or the session is replaced.</summary>
    protected DetailState? UseDetailState() =>
        UseExternalStore(DetailStateHook.Subscribe, DetailStateHook.Snapshot, DetailStateHook.SameState);

    /// <summary>The current engine session's state, as <see cref="EngineStateComponent"/> gives it.</summary>
    protected EngineState UseEngineState() =>
        UseExternalStore(EngineStateHook.Subscribe, EngineStateHook.Snapshot, EngineStateHook.SameState);
}

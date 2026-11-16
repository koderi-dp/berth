using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace Berth.Hooks;

/// <summary>
/// The engine state as an external store for <c>UseExternalStore</c>: the snapshot is the current session's state, and a subscriber
/// hears of every change of it on the UI thread (<see cref="UiThreadSubscription{TSource, TState}"/>), also across a switch of
/// connection, which replaces the session.
/// </summary>
/// <remarks>
/// Reactor keeps a class component's hooks protected and its render context internal, so a hook cannot be an extension method on
/// <see cref="Component"/>; class components get it from <see cref="EngineStateComponent"/>.
/// </remarks>
internal static class EngineStateHook
{
    /// <summary>Subscribes <c>onChange</c> to the current session (and each one that replaces it); returns the unsubscribe action.</summary>
    public static readonly Func<Action, Action> Subscribe = onChange =>
        UiThreadSubscription<EngineSession, EngineState>.Start(
            onChange,
            () => AppServices.Current.Session,
            handler => AppServices.Current.SessionChanged += handler,
            handler => AppServices.Current.SessionChanged -= handler,
            (session, handler) => session.StateChanged += handler,
            (session, handler) => session.StateChanged -= handler);

    public static readonly Func<EngineState> Snapshot = () => AppServices.Current.Session.State;

    /// <summary>The reducer returns the same instance when nothing changed, so identity is the right test, and a cheap one.</summary>
    public static readonly IEqualityComparer<EngineState> SameState = ReferenceEqualityComparer.Instance;
}

/// <summary>A component that shows the engine state.</summary>
internal abstract class EngineStateComponent : Component
{
    /// <summary>The current session's state. Re-renders on the UI thread when it changes or the session is replaced.</summary>
    protected EngineState UseEngineState() =>
        UseExternalStore(EngineStateHook.Subscribe, EngineStateHook.Snapshot, EngineStateHook.SameState);
}

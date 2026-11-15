using Berth.Session;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace Berth.Hooks;

/// <summary>
/// The engine state as an external store for <c>UseExternalStore</c>: the snapshot is the current session's state, and a subscriber
/// hears of every change of it on the UI thread, also across a switch of connection, which replaces the session.
/// </summary>
/// <remarks>
/// Reactor keeps a class component's hooks protected and its render context internal, so a hook cannot be an extension method on
/// <see cref="Component"/>; class components get it from <see cref="EngineStateComponent"/>.
/// </remarks>
internal static class EngineStateHook
{
    /// <summary>Subscribes <c>onChange</c> to the current session (and each one that replaces it); returns the unsubscribe action.</summary>
    public static readonly Func<Action, Action> Subscribe = onChange =>
        new Subscription(onChange, ReactorApp.UIDispatcher ?? throw new InvalidOperationException("The UI thread is not running.")).Dispose;

    public static readonly Func<EngineState> Snapshot = () => AppServices.Current.Session.State;

    /// <summary>The reducer returns the same instance when nothing changed, so identity is the right test, and a cheap one.</summary>
    public static readonly IEqualityComparer<EngineState> SameState = ReferenceEqualityComparer.Instance;

    /// <summary>
    /// Follows the current session. The session raises its changes on a thread-pool thread, often many in a row; they are coalesced
    /// into at most one pending UI-thread callback, which reads the latest state when it runs.
    /// </summary>
    private sealed class Subscription
    {
        private readonly Lock _gate = new();
        private readonly Action _onChange;
        private readonly DispatcherQueue _ui;
        private EngineSession? _session;
        private int _pending;
        private bool _disposed;

        public Subscription(Action onChange, DispatcherQueue ui)
        {
            _onChange = onChange;
            _ui = ui;
            AppServices.Current.SessionChanged += OnSessionChanged;
            OnSessionChanged();
        }

        public void Dispose()
        {
            AppServices.Current.SessionChanged -= OnSessionChanged;
            lock (_gate)
            {
                _disposed = true;
                _session?.StateChanged -= OnStateChanged;
                _session = null;
            }
        }

        /// <summary>Moves to the current session and reports a change: the new session's state replaces the old one's.</summary>
        private void OnSessionChanged()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _session?.StateChanged -= OnStateChanged;
                _session = AppServices.Current.Session;
                _session.StateChanged += OnStateChanged;
            }

            // Also covers a change between the render that read the snapshot and this subscription.
            Post();
        }

        private void OnStateChanged(EngineState _) => Post();

        private void Post()
        {
            if (Interlocked.Exchange(ref _pending, 1) == 0 && !_ui.TryEnqueue(Deliver))
                Volatile.Write(ref _pending, 0); // the UI thread is shutting down: nothing left to update
        }

        private void Deliver()
        {
            // Cleared before the callback, so a change that arrives while the UI reads the state queues another update.
            Volatile.Write(ref _pending, 0);
            if (!Volatile.Read(ref _disposed))
                _onChange();
        }
    }
}

/// <summary>A component that shows the engine state.</summary>
internal abstract class EngineStateComponent : Component
{
    /// <summary>The current session's state. Re-renders on the UI thread when it changes or the session is replaced.</summary>
    protected EngineState UseEngineState() =>
        UseExternalStore(EngineStateHook.Subscribe, EngineStateHook.Snapshot, EngineStateHook.SameState);
}

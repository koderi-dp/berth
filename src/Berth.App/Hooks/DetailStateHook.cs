using Berth.Session;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace Berth.Hooks;

/// <summary>
/// The open container detail session's state as an external store for <c>UseExternalStore</c>: the snapshot is the state of
/// <see cref="AppServices.Detail"/> (null while none is open), and a subscriber hears of every change of it on the UI thread, also
/// when the session is replaced or closed.
/// </summary>
internal static class DetailStateHook
{
    /// <summary>Subscribes <c>onChange</c> to the open detail session (and each one that replaces it); returns the unsubscribe action.</summary>
    public static readonly Func<Action, Action> Subscribe = onChange =>
        new Subscription(onChange, ReactorApp.UIDispatcher ?? throw new InvalidOperationException("The UI thread is not running.")).Dispose;

    public static readonly Func<DetailState?> Snapshot = () => AppServices.Current.Detail?.State;

    /// <summary>The reducer returns the same instance when nothing changed, so identity is the right test, and a cheap one.</summary>
    public static readonly IEqualityComparer<DetailState?> SameState = ReferenceEqualityComparer.Instance;

    /// <summary>
    /// Follows the open detail session. The session raises its changes on a thread-pool thread, up to ten a second while a log
    /// floods; they are coalesced into at most one pending UI-thread callback, which reads the latest state when it runs.
    /// </summary>
    private sealed class Subscription
    {
        private readonly Lock _gate = new();
        private readonly Action _onChange;
        private readonly DispatcherQueue _ui;
        private ContainerDetailSession? _session;
        private int _pending;
        private bool _disposed;

        public Subscription(Action onChange, DispatcherQueue ui)
        {
            _onChange = onChange;
            _ui = ui;
            AppServices.Current.DetailChanged += OnDetailChanged;
            OnDetailChanged();
        }

        public void Dispose()
        {
            AppServices.Current.DetailChanged -= OnDetailChanged;
            lock (_gate)
            {
                _disposed = true;
                _session?.StateChanged -= OnStateChanged;
                _session = null;
            }
        }

        /// <summary>Moves to the open session (or none) and reports a change: its state replaces the old one's.</summary>
        private void OnDetailChanged()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _session?.StateChanged -= OnStateChanged;
                _session = AppServices.Current.Detail;
                _session?.StateChanged += OnStateChanged;
            }

            // Also covers a change between the render that read the snapshot and this subscription.
            Post();
        }

        private void OnStateChanged(DetailState _) => Post();

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

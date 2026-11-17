using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;

namespace Berth.Hooks;

/// <summary>
/// Follows the state changes of a source that can be replaced (the engine session, the open detail session) and tells a
/// subscriber on the UI thread. The source raises its changes on a thread-pool thread, often many in a row; they are coalesced
/// into at most one pending UI-thread callback, which reads the latest state when it runs. When the source is replaced (or goes,
/// for a source that can be null) the subscription moves to the new one and reports a change, since the new state replaces the old.
/// </summary>
/// <typeparam name="TSource">What raises the changes.</typeparam>
/// <typeparam name="TState">What a change carries (not used: the subscriber reads the latest state itself).</typeparam>
internal sealed class UiThreadSubscription<TSource, TState>
    where TSource : class
{
    private readonly Lock _lock = new();
    private readonly Action _onChange;
    private readonly DispatcherQueue _ui;
    private readonly Func<TSource?> _current;
    private readonly Action<Action> _removeReplaced;
    private readonly Action<TSource, Action<TState>> _addChanged;
    private readonly Action<TSource, Action<TState>> _removeChanged;
    private readonly RenderGate? _gate;
    private TSource? _source;
    private int _pending;
    private bool _disposed;
    // A change arrived while the window was hidden to the tray and was held back.
    private bool _heldBack;

    /// <param name="onChange">Called on the UI thread after one or more changes.</param>
    /// <param name="current">The source now; null when there is none.</param>
    /// <param name="addReplaced">Subscribes to the event raised when the source is replaced; <paramref name="removeReplaced"/> undoes it.</param>
    /// <param name="addChanged">Subscribes to a source's state changes; <paramref name="removeChanged"/> undoes it.</param>
    private UiThreadSubscription(
        Action onChange,
        Func<TSource?> current,
        Action<Action> addReplaced,
        Action<Action> removeReplaced,
        Action<TSource, Action<TState>> addChanged,
        Action<TSource, Action<TState>> removeChanged,
        RenderGate? gate)
    {
        _onChange = onChange;
        _gate = gate;
        _ui = ReactorApp.UIDispatcher ?? throw new InvalidOperationException("The UI thread is not running.");
        _current = current;
        _removeReplaced = removeReplaced;
        _addChanged = addChanged;
        _removeChanged = removeChanged;
        addReplaced(OnReplaced);
        Shell.MainWindow.HiddenChanged += OnHiddenChanged;
        if (gate is not null)
            gate.Changed += OnHiddenChanged;
        OnReplaced();
    }

    /// <summary>
    /// Starts following; returns the action that stops it (for <c>UseExternalStore</c>'s subscribe). With a <paramref name="gate"/>,
    /// changes are also held back while it is closed (the page that shows them is not the one shown).
    /// </summary>
    /// <inheritdoc cref="UiThreadSubscription{TSource, TState}(Action, Func{TSource}, Action{Action}, Action{Action}, Action{TSource, Action{TState}}, Action{TSource, Action{TState}}, RenderGate)"/>
    public static Action Start(
        Action onChange,
        Func<TSource?> current,
        Action<Action> addReplaced,
        Action<Action> removeReplaced,
        Action<TSource, Action<TState>> addChanged,
        Action<TSource, Action<TState>> removeChanged,
        RenderGate? gate = null) =>
        new UiThreadSubscription<TSource, TState>(onChange, current, addReplaced, removeReplaced, addChanged, removeChanged, gate).Dispose;

    private void Dispose()
    {
        _removeReplaced(OnReplaced);
        Shell.MainWindow.HiddenChanged -= OnHiddenChanged;
        if (_gate is not null)
            _gate.Changed -= OnHiddenChanged;
        lock (_lock)
        {
            _disposed = true;
            if (_source is not null)
                _removeChanged(_source, OnStateChanged);
            _source = null;
        }
    }

    /// <summary>Moves to the current source (or none) and reports a change: its state replaces the old one's.</summary>
    private void OnReplaced()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            if (_source is not null)
                _removeChanged(_source, OnStateChanged);
            _source = _current();
            if (_source is not null)
                _addChanged(_source, OnStateChanged);
        }

        // Also covers a change between the render that read the snapshot and this subscription.
        Post();
    }

    private void OnStateChanged(TState _) => Post();

    private void Post()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0 && !_ui.TryEnqueue(Deliver))
            Volatile.Write(ref _pending, 0); // the UI thread is shutting down: nothing left to update
    }

    private void Deliver()
    {
        // Cleared before the callback, so a change that arrives while the UI reads the state queues another update.
        Volatile.Write(ref _pending, 0);
        if (Volatile.Read(ref _disposed))
            return;

        // A window hidden to the tray does no rendering work: the change is held back, and the latest state is read when it shows.
        if (Shell.MainWindow.IsHidden || _gate is { IsOpen: false })
        {
            _heldBack = true;
            return;
        }

        _onChange();
    }

    /// <summary>On the UI thread: the window was shown again (or the gate opened), so a change held back meanwhile is delivered now.</summary>
    private void OnHiddenChanged()
    {
        if (!Shell.MainWindow.IsHidden && _gate is not { IsOpen: false } && _heldBack)
        {
            _heldBack = false;
            Post();
        }
    }
}

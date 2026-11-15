using System.Collections.Concurrent;
using Berth.Engine;
using Berth.Engine.Models;
using Berth.Session;
using Microsoft.Extensions.Time.Testing;

namespace Berth.Tests.Session;

public sealed class EngineSessionTests
{
    /// <summary>Only a safety net so a broken runtime fails the test instead of hanging it; tests never wait for time.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static EngineEventMessage Event(string type, string action) =>
        new(type, action, "abc", new Dictionary<string, string>(), 0);

    [Fact]
    public async Task Connect_ends_connected_with_the_containers_loaded()
    {
        var api = new FakeEngineApi();
        await using var h = Harness.With(api);

        h.Session.Dispatch(new ConnectRequested());
        var state = await h.WaitFor(s => s.Status == ConnectionStatus.Connected && s.Containers.Length == 2);

        Assert.Equal(FakeEngineApi.DefaultVersion, state.Version);
        Assert.Equal(FakeEngineApi.DefaultInfo, state.Info);
        Assert.Equal(["a", "b"], state.Containers.Select(c => c.Id));
        Assert.Null(state.ConnectionError);
        await api.StreamStarted.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_failed_connect_retries_after_one_second_and_succeeds()
    {
        var refused = new FakeEngineApi();
        refused.Fail("Connect", new EngineException(0, "connection refused"));
        var good = new FakeEngineApi();
        await using var h = Harness.With(refused, good);

        h.Session.Dispatch(new ConnectRequested());
        var failed = await h.WaitFor(s => s.Status == ConnectionStatus.Disconnected && s.RetryAttempt == 1);
        Assert.Equal("connection refused", failed.ConnectionError);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        var state = await h.WaitFor(s => s.Status == ConnectionStatus.Connected && s.Containers.Length == 2);

        Assert.Null(state.ConnectionError);
        Assert.Equal(0, state.RetryAttempt);
        Assert.Equal(2, h.Created.Count);
        Assert.True(refused.IsDisposed);
    }

    [Fact]
    public async Task An_engine_event_reloads_the_list_after_the_debounce()
    {
        var api = new FakeEngineApi();
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        api.Containers = [FakeEngineApi.Container("a"), FakeEngineApi.Container("b"), FakeEngineApi.Container("c")];
        api.Push(Event("container", "start"));
        await h.WaitFor(s => s.ReloadScheduled);

        h.Time.Advance(SessionReducer.ReloadDebounce);
        var state = await h.WaitFor(s => !s.ReloadScheduled && s.Containers.Length == 3);

        Assert.Equal(["a", "b", "c"], state.Containers.Select(c => c.Id));
    }

    [Fact]
    public async Task An_action_calls_the_engine_and_the_row_is_pending_meanwhile()
    {
        var api = new FakeEngineApi();
        var stop = api.Hold("Stop");
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        h.Session.Dispatch(new ActionRequested(["a"], ContainerAction.Stop));
        await stop.Entered.WaitAsync(Patience);

        Assert.Equal(ContainerAction.Stop, h.Session.State.Pending["a"]);
        Assert.Contains("Stop a", api.Calls);

        stop.Release();
        var state = await h.WaitFor(s => s.Pending.IsEmpty);

        Assert.True(state.ReloadScheduled);
        Assert.Null(state.ActionError);
    }

    [Fact]
    public async Task Remove_passes_force_to_the_engine()
    {
        var api = new FakeEngineApi();
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        h.Session.Dispatch(new ActionRequested(["a"], ContainerAction.Remove, Force: true));
        await h.WaitFor(s => s.Pending.IsEmpty && s.ReloadScheduled);

        Assert.Contains("Remove a force=True", api.Calls);
    }

    [Fact]
    public async Task An_action_failure_shows_the_engine_message_as_is()
    {
        var api = new FakeEngineApi();
        api.Fail("Stop", new EngineException(409, "container a is not running"));
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        h.Session.Dispatch(new ActionRequested(["a"], ContainerAction.Stop));
        var state = await h.WaitFor(s => s.ActionError is not null);

        Assert.Equal("container a is not running", state.ActionError);
        Assert.Equal(ConnectionStatus.Connected, state.Status);
        Assert.Empty(state.Pending);
    }

    [Fact]
    public async Task An_action_that_cannot_reach_the_engine_moves_to_disconnected()
    {
        var api = new FakeEngineApi();
        api.Fail("Stop", new EngineException(0, "connection reset"));
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        h.Session.Dispatch(new ActionRequested(["a"], ContainerAction.Stop));
        var state = await h.WaitFor(s => s.Status == ConnectionStatus.Disconnected);

        Assert.Equal("connection reset", state.ConnectionError);
        Assert.Null(state.ActionError);
    }

    [Fact]
    public async Task The_event_stream_ending_moves_to_disconnected_and_reconnects()
    {
        var first = new FakeEngineApi();
        var second = new FakeEngineApi();
        await using var h = Harness.With(first, second);
        await h.ConnectAsync();
        await first.StreamStarted.WaitAsync(Patience);

        first.EndStream();
        var lost = await h.WaitFor(s => s.Status == ConnectionStatus.Disconnected);
        Assert.Equal("The engine closed the connection.", lost.ConnectionError);
        Assert.Equal(1, lost.RetryAttempt);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.WaitFor(s => s.Status == ConnectionStatus.Connected);

        await second.StreamStarted.WaitAsync(Patience);
        Assert.Equal(2, h.Created.Count);
    }

    [Fact]
    public async Task The_event_stream_failing_reports_its_message()
    {
        var api = new FakeEngineApi();
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        await api.StreamStarted.WaitAsync(Patience);

        api.EndStream(new EngineException(0, "connection reset by peer"));
        var state = await h.WaitFor(s => s.Status == ConnectionStatus.Disconnected);

        Assert.Equal("connection reset by peer", state.ConnectionError);
    }

    [Fact]
    public async Task An_exception_in_one_effect_does_not_stop_later_events()
    {
        var api = new FakeEngineApi();
        api.Fail("List", new InvalidOperationException("list broke"));
        var created = 0;
        await using var h = new Harness(() =>
            Interlocked.Increment(ref created) == 1 ? throw new InvalidOperationException("factory broke") : api);

        h.Session.Dispatch(new ConnectRequested());
        var failed = await h.WaitFor(s => s.Status == ConnectionStatus.Disconnected && s.RetryAttempt == 1);
        Assert.Equal("factory broke", failed.ConnectionError);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        var loadFailed = await h.WaitFor(s => s.ActionError is not null);
        Assert.Equal("list broke", loadFailed.ActionError);
        Assert.Equal(ConnectionStatus.Connected, loadFailed.Status);

        api.Fail("List", new InvalidOperationException("still broken"));
        api.Push(Event("container", "die"));
        await h.WaitFor(s => s.ReloadScheduled);
        h.Time.Advance(SessionReducer.ReloadDebounce);
        var again = await h.WaitFor(s => s.ActionError == "still broken");

        Assert.Equal(ConnectionStatus.Connected, again.Status);
    }

    [Fact]
    public async Task A_superseded_connect_reports_nothing()
    {
        var slow = new FakeEngineApi { IgnoreCancellation = true, Version = FakeEngineApi.DefaultVersion with { Version = "old" } };
        var slowConnect = slow.Hold("Connect");
        var fresh = new FakeEngineApi { Version = FakeEngineApi.DefaultVersion with { Version = "new" } };
        var freshConnect = fresh.Hold("Connect");
        await using var h = Harness.With(slow, fresh);

        h.Session.Dispatch(new ConnectRequested());
        await slowConnect.Entered.WaitAsync(Patience);
        h.Session.Dispatch(new ConnectRequested());
        await freshConnect.Entered.WaitAsync(Patience);

        var dropped = h.WaitForDropped<ConnectSucceeded>();
        slowConnect.Release();
        await dropped;

        Assert.Equal(ConnectionStatus.Connecting, h.Session.State.Status);
        Assert.Null(h.Session.State.Version);

        freshConnect.Release();
        var state = await h.WaitFor(s => s.Status == ConnectionStatus.Connected);

        Assert.Equal("new", state.Version?.Version);
    }

    [Fact]
    public async Task A_stream_stopped_by_the_session_does_not_report_a_lost_connection()
    {
        var first = new FakeEngineApi { IgnoreCancellation = true };
        var second = new FakeEngineApi();
        await using var h = Harness.With(first, second);
        await h.ConnectAsync();
        await first.StreamStarted.WaitAsync(Patience);

        h.Session.Dispatch(new ConnectRequested());
        await h.WaitFor(s => s.Status == ConnectionStatus.Connected && s.Containers.Length == 2 && h.Created.Count == 2);
        await second.StreamStarted.WaitAsync(Patience);

        var dropped = h.WaitForDropped<ConnectionLost>();
        first.EndStream();
        await dropped;

        Assert.Equal(ConnectionStatus.Connected, h.Session.State.Status);
        Assert.Null(h.Session.State.ConnectionError);
    }

    [Fact]
    public async Task Dispose_cancels_in_flight_calls_and_raises_no_further_state()
    {
        var api = new FakeEngineApi();
        var stop = api.Hold("Stop");
        var h = Harness.With(api);
        await h.ConnectAsync();
        await api.StreamStarted.WaitAsync(Patience);
        h.Session.Dispatch(new ActionRequested(["a"], ContainerAction.Stop));
        await stop.Entered.WaitAsync(Patience);

        await h.DisposeAsync();
        var raised = 0;
        h.Session.StateChanged += _ => Interlocked.Increment(ref raised);
        h.Session.Dispatch(new ConnectRequested());
        api.Push(Event("container", "start"));
        stop.Release();

        Assert.Contains("Stop a", api.Cancelled);
        Assert.True(api.IsDisposed);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task State_changes_arrive_in_order()
    {
        var ids = Enumerable.Range(0, 50).Select(i => $"c{i}").ToArray();
        var api = new FakeEngineApi { Containers = ids.Select(FakeEngineApi.Container).ToArray() };
        api.Hold("Stop");
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        var seen = new ConcurrentQueue<int>();
        h.Session.StateChanged += s => seen.Enqueue(s.Pending.Count);
        foreach (var id in ids)
            h.Session.Dispatch(new ActionRequested([id], ContainerAction.Stop));
        await h.WaitFor(s => s.Pending.Count == ids.Length);

        Assert.Equal(Enumerable.Range(1, ids.Length), seen);
    }

    /// <summary>A session over fake engines, with a fake clock.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<IEngineApi> _created = new();

        public Harness(Func<IEngineApi> createApi)
        {
            Session = new EngineSession(
                () =>
                {
                    var api = createApi();
                    _created.Enqueue(api);
                    return api;
                },
                Time);
        }

        public FakeTimeProvider Time { get; } = new();

        public EngineSession Session { get; }

        public IReadOnlyCollection<IEngineApi> Created => _created;

        /// <summary>Hands out <paramref name="apis"/> in order, then fresh default fakes.</summary>
        public static Harness With(params FakeEngineApi[] apis)
        {
            var queue = new ConcurrentQueue<FakeEngineApi>(apis);
            return new Harness(() => queue.TryDequeue(out var api) ? api : new FakeEngineApi());
        }

        public Task<EngineState> ConnectAsync()
        {
            Session.Dispatch(new ConnectRequested());
            return WaitFor(s => s.Status == ConnectionStatus.Connected && s.Containers.Length > 0);
        }

        /// <summary>Completes with the first state, current or later, that matches <paramref name="match"/>.</summary>
        public async Task<EngineState> WaitFor(Func<EngineState, bool> match)
        {
            var found = new TaskCompletionSource<EngineState>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Check(EngineState s)
            {
                if (match(s))
                    found.TrySetResult(s);
            }

            Session.StateChanged += Check;
            try
            {
                Check(Session.State);
                return await found.Task.WaitAsync(Patience);
            }
            finally
            {
                Session.StateChanged -= Check;
            }
        }

        /// <summary>Completes when the session drops an event of type <typeparamref name="T"/> from an older connection.</summary>
        public Task WaitForDropped<T>() where T : SessionEvent
        {
            var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Session.StaleEventDropped += e =>
            {
                if (e is T)
                    dropped.TrySetResult();
            };
            return dropped.Task.WaitAsync(Patience);
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }
}

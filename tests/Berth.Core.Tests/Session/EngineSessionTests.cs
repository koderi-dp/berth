using System.Collections.Concurrent;
using Berth.Engine;
using Berth.Engine.Models;
using Berth.Session;
using Berth.Text;
using Microsoft.Extensions.Time.Testing;

namespace Berth.Tests.Session;

public sealed partial class EngineSessionTests
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
    public async Task Copying_the_run_command_inspects_the_container_and_hands_over_the_command()
    {
        var api = new FakeEngineApi();
        var inspection = FakeEngineApi.Inspection("a");
        api.SetInspection(inspection);
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        var copied = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Session.CopyRequested += text => copied.TrySetResult(text);

        h.Session.Dispatch(new RunCommandRequested("a", ShellKind.Posix));
        var text = await copied.Task.WaitAsync(Patience);

        Assert.Equal(DockerRunCommand.Build(inspection.Details, ShellKind.Posix), text);
        Assert.Contains("Inspect a", api.Calls);
        Assert.Null(h.Session.State.ActionError);
    }

    [Fact]
    public async Task Copying_the_run_command_of_a_container_that_is_gone_shows_the_engine_message_as_is()
    {
        var api = new FakeEngineApi();
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        var copies = 0;
        h.Session.CopyRequested += _ => Interlocked.Increment(ref copies);

        h.Session.Dispatch(new RunCommandRequested("gone", ShellKind.PowerShell));
        var state = await h.WaitFor(s => s.ActionError is not null);

        Assert.Equal("No such container: gone", state.ActionError);
        Assert.Equal(ConnectionStatus.Connected, state.Status);
        Assert.Equal(0, copies);
        var (what, _) = await h.WaitForError();
        Assert.Equal("inspecting a container for its run command failed", what);
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

    [Fact]
    public async Task A_failed_connect_is_reported_with_its_exception()
    {
        var refused = new EngineException(0, "connection refused");
        var api = new FakeEngineApi();
        api.Fail("Connect", refused);
        await using var h = Harness.With(api);

        h.Session.Dispatch(new ConnectRequested());
        var error = await h.WaitForError();

        Assert.Equal("connecting to the engine failed", error.What);
        Assert.Same(refused, error.Exception);
    }

    [Fact]
    public async Task A_failed_action_is_reported_with_its_exception()
    {
        var conflict = new EngineException(409, "container a is not running");
        var api = new FakeEngineApi();
        api.Fail("Stop", conflict);
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        h.Session.Dispatch(new ActionRequested(["a"], ContainerAction.Stop));
        var error = await h.WaitForError();

        Assert.Equal("a container action failed", error.What);
        Assert.Same(conflict, error.Exception);
    }

    [Fact]
    public async Task A_failed_load_is_reported_with_its_exception()
    {
        var broken = new InvalidOperationException("list broke");
        var api = new FakeEngineApi();
        api.Fail("List", broken);
        await using var h = Harness.With(api);

        h.Session.Dispatch(new ConnectRequested());
        var error = await h.WaitForError();

        Assert.Equal("loading the containers failed", error.What);
        Assert.Same(broken, error.Exception);
    }

    [Fact]
    public async Task A_failed_event_stream_is_reported_with_its_exception()
    {
        var reset = new EngineException(0, "connection reset by peer");
        var api = new FakeEngineApi();
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        await api.StreamStarted.WaitAsync(Patience);

        api.EndStream(reset);
        var error = await h.WaitForError();

        Assert.Equal("the engine event stream failed", error.What);
        Assert.Same(reset, error.Exception);
    }

    [Fact]
    public async Task A_throwing_state_handler_is_reported_and_later_states_still_arrive()
    {
        var api = new FakeEngineApi();
        await using var h = Harness.With(api);
        var thrown = new InvalidOperationException("handler broke");
        h.Session.StateChanged += _ => throw thrown;

        h.Session.Dispatch(new ConnectRequested());
        var error = await h.WaitForError();
        var state = await h.WaitFor(s => s.Status == ConnectionStatus.Connected && s.Containers.Length == 2);

        Assert.Equal("a state change handler failed", error.What);
        Assert.Same(thrown, error.Exception);
        Assert.Equal(["a", "b"], state.Containers.Select(c => c.Id));
    }

    [Fact]
    public async Task An_effect_that_cannot_start_is_reported_and_the_loop_keeps_running()
    {
        var api = new FakeEngineApi();
        api.Fail("Connect", new EngineException(0, "connection refused"));
        var time = new BrokenTimerTimeProvider();
        await using var h = new Harness(() => api, time);

        h.Session.Dispatch(new ConnectRequested());
        await h.WaitFor(s => s.Status == ConnectionStatus.Disconnected && s.RetryAttempt == 1);
        var error = await h.WaitForError(e => e.What == "handling a session event failed");
        Assert.IsType<InvalidOperationException>(error.Exception);

        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Session.StateChanged += s =>
        {
            if (s.Status == ConnectionStatus.Connecting)
                raised.TrySetResult();
        };
        h.Session.Dispatch(new ConnectRequested());
        await raised.Task.WaitAsync(Patience);
    }

    [Fact]
    public async Task Cancellation_caused_by_the_session_is_not_reported()
    {
        var first = new FakeEngineApi();
        var stop = first.Hold("Stop");
        var h = Harness.With(first);
        await h.ConnectAsync();
        await first.StreamStarted.WaitAsync(Patience);
        h.Session.Dispatch(new ActionRequested(["a"], ContainerAction.Stop));
        await stop.Entered.WaitAsync(Patience);

        // Starting over cancels the held action and the stream; disposing cancels the new connection's calls.
        h.Session.Dispatch(new ConnectRequested());
        await h.WaitFor(s => s.Status == ConnectionStatus.Connected && h.Created.Count == 2);
        await h.DisposeAsync();

        Assert.Contains("Stop a", first.Cancelled);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task No_detail_session_opens_while_not_connected()
    {
        var api = new FakeEngineApi();
        var connect = api.Hold("Connect");
        await using var h = Harness.With(api);

        Assert.Null(h.Session.OpenDetail("a"));

        h.Session.Dispatch(new ConnectRequested());
        await connect.Entered.WaitAsync(Patience);
        Assert.Null(h.Session.OpenDetail("a"));
    }

    [Fact]
    public async Task A_detail_session_runs_on_the_connection_and_is_cut_off_when_the_connection_is_replaced()
    {
        var first = new FakeEngineApi();
        first.SetInspection(FakeEngineApi.Inspection("a"));
        await using var h = Harness.With(first);
        await h.ConnectAsync();

        await using var detail = h.Session.OpenDetail("a");
        Assert.NotNull(detail);
        detail.Open();
        await first.Logs.WhenOpened().WaitAsync(Patience);
        await first.Stats.WhenOpened().WaitAsync(Patience);
        Assert.Contains("Inspect a", first.Calls);

        h.Session.Dispatch(new ConnectRequested());
        var state = await ContainerDetailSessionTests.WaitFor(detail, s => s.ConnectionLost);
        await h.WaitFor(s => s.Status == ConnectionStatus.Connected && h.Created.Count == 2);

        // The old client is disposed only once the detail page's streams on it have stopped.
        Assert.True(first.IsDisposed);
        Assert.Contains("Logs a tail=1000 follow=True tty=False", first.Cancelled);
        Assert.Contains("Stats a", first.Cancelled);
        Assert.Equal(StreamStatus.Ended, state.LogStatus);
        Assert.Equal(StreamStatus.Ended, state.StatsStatus);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task A_detail_session_closed_by_the_page_is_not_cut_off_again()
    {
        var api = new FakeEngineApi();
        api.SetInspection(FakeEngineApi.Inspection("a"));
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        var detail = h.Session.OpenDetail("a");
        Assert.NotNull(detail);
        detail.Open();
        await api.Logs.WhenOpened().WaitAsync(Patience);
        await detail.DisposeAsync();

        h.Session.Dispatch(new ConnectRequested());
        await h.WaitFor(s => s.Status == ConnectionStatus.Connected && h.Created.Count == 2);

        Assert.False(detail.State.ConnectionLost);
        Assert.Empty(h.Errors);
    }

    private static StatsSample Sample(int second, double cpu) =>
        new(new DateTimeOffset(2026, 11, 15, 8, 0, second, TimeSpan.Zero), cpu, 1024, 4096, 0, 0, 0, 0, 1);

    [Fact]
    public async Task List_stats_arrive_as_one_batch_a_second_with_the_latest_sample_of_each_container()
    {
        var api = new FakeEngineApi();
        var a = api.StatsOf("a");
        var b = api.StatsOf("b");
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        h.Session.Dispatch(new StatsWantedChanged(true));
        await a.WhenOpened().WaitAsync(Patience);
        await b.WhenOpened().WaitAsync(Patience);
        var recorded = h.WaitForStatsRecorded(5);
        a.Push(Sample(0, cpu: 1));
        a.Push(Sample(1, cpu: 2));
        a.Push(Sample(2, cpu: 3));
        b.Push(Sample(0, cpu: 10));
        b.Push(Sample(1, cpu: 20));
        await recorded;

        var changes = 0;
        h.Session.StateChanged += _ => Interlocked.Increment(ref changes);
        h.Time.Advance(EngineSession.StatsBatchInterval);
        var state = await h.WaitFor(s => s.Stats.Count == 2);

        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Equal(3, state.Stats["a"].CpuPercent);
        Assert.Equal(20, state.Stats["b"].CpuPercent);

        recorded = h.WaitForStatsRecorded(1);
        b.Push(Sample(2, cpu: 30));
        await recorded;
        h.Time.Advance(EngineSession.StatsBatchInterval);
        var next = await h.WaitFor(s => s.Stats["b"].CpuPercent == 30);

        Assert.Equal(2, Volatile.Read(ref changes));
        Assert.Equal(3, next.Stats["a"].CpuPercent);
    }

    [Fact]
    public async Task A_stats_stream_that_fails_for_one_container_does_not_stop_the_others()
    {
        var api = new FakeEngineApi();
        var a = api.StatsOf("a");
        var b = api.StatsOf("b");
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        h.Session.Dispatch(new StatsWantedChanged(true));
        await a.WhenOpened().WaitAsync(Patience);
        await b.WhenOpened().WaitAsync(Patience);

        var broken = new EngineException(500, "stats broke");
        a.End(broken);
        var error = await h.WaitForError();
        var recorded = h.WaitForStatsRecorded(1);
        b.Push(Sample(0, cpu: 10));
        await recorded;
        h.Time.Advance(EngineSession.StatsBatchInterval);
        var state = await h.WaitFor(s => s.Stats.ContainsKey("b"));

        Assert.Equal("a container stats stream failed", error.What);
        Assert.Same(broken, error.Exception);
        Assert.Equal(ConnectionStatus.Connected, state.Status);
        Assert.Null(state.ConnectionError);
        Assert.False(state.Stats.ContainsKey("a"));
    }

    [Fact]
    public async Task A_stats_stream_that_ends_because_its_container_stopped_is_not_an_error()
    {
        var api = new FakeEngineApi();
        var a = api.StatsOf("a");
        var b = api.StatsOf("b");
        a.End();
        b.End(new EngineException(404, "No such container: b"));
        var h = Harness.With(api);
        await h.ConnectAsync();

        h.Session.Dispatch(new StatsWantedChanged(true));
        await a.WhenOpened().WaitAsync(Patience);
        await b.WhenOpened().WaitAsync(Patience);
        await h.DisposeAsync();

        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task A_list_change_starts_and_stops_only_the_difference()
    {
        var api = new FakeEngineApi();
        var a = api.StatsOf("a");
        var b = api.StatsOf("b");
        var c = api.StatsOf("c");
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        h.Session.Dispatch(new StatsWantedChanged(true));
        await a.WhenOpened().WaitAsync(Patience);
        await b.WhenOpened().WaitAsync(Patience);

        api.Containers = [FakeEngineApi.Container("b"), FakeEngineApi.Container("c")];
        h.Session.Dispatch(new ReloadRequested());
        await c.WhenOpened().WaitAsync(Patience);
        await api.WhenCancelled("Stats a").WaitAsync(Patience);

        Assert.Equal(1, b.Opened);
        Assert.DoesNotContain("Stats b", api.Cancelled);
        Assert.Equal(1, api.Calls.Count(call => call == "Stats b"));
    }

    [Fact]
    public async Task Turning_list_stats_off_stops_every_stream_and_clears_them()
    {
        var api = new FakeEngineApi();
        var a = api.StatsOf("a");
        var b = api.StatsOf("b");
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        h.Session.Dispatch(new StatsWantedChanged(true));
        await a.WhenOpened().WaitAsync(Patience);
        await b.WhenOpened().WaitAsync(Patience);
        var recorded = h.WaitForStatsRecorded(1);
        a.Push(Sample(0, cpu: 1));
        await recorded;
        h.Time.Advance(EngineSession.StatsBatchInterval);
        await h.WaitFor(s => s.Stats.Count == 1);

        h.Session.Dispatch(new StatsWantedChanged(false));
        var state = await h.WaitFor(s => !s.StatsWanted);
        await api.WhenCancelled("Stats a").WaitAsync(Patience);
        await api.WhenCancelled("Stats b").WaitAsync(Patience);

        Assert.Empty(state.Stats);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task Losing_the_connection_stops_the_list_stats()
    {
        var first = new FakeEngineApi();
        var a = first.StatsOf("a");
        await using var h = Harness.With(first);
        await h.ConnectAsync();
        await first.StreamStarted.WaitAsync(Patience);
        h.Session.Dispatch(new StatsWantedChanged(true));
        await a.WhenOpened().WaitAsync(Patience);

        first.EndStream(new EngineException(0, "connection reset by peer"));
        var state = await h.WaitFor(s => s.Status == ConnectionStatus.Disconnected);
        await first.WhenCancelled("Stats a").WaitAsync(Patience);

        Assert.Empty(state.Stats);
        Assert.True(state.StatsWanted);
    }

    /// <summary>A fake clock whose first timer cannot be created, so starting the effect that needs it throws.</summary>
    private sealed class BrokenTimerTimeProvider : FakeTimeProvider
    {
        private int _created;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Interlocked.Increment(ref _created) == 1
                ? throw new InvalidOperationException("no timer")
                : base.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>A session over fake engines, with a fake clock.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<IEngineApi> _created = new();
        private readonly ConcurrentQueue<(string What, Exception Exception)> _errors = new();
        private readonly SemaphoreSlim _errorArrived = new(0);

        public Harness(Func<IEngineApi> createApi, FakeTimeProvider? time = null)
        {
            Time = time ?? new FakeTimeProvider();
            Session = new EngineSession(
                () =>
                {
                    var api = createApi();
                    _created.Enqueue(api);
                    return api;
                },
                Time,
                (what, ex) =>
                {
                    _errors.Enqueue((what, ex));
                    _errorArrived.Release();
                });
        }

        public FakeTimeProvider Time { get; }

        public EngineSession Session { get; }

        public IReadOnlyCollection<IEngineApi> Created => _created;

        /// <summary>Everything the session reported to its error sink, in order.</summary>
        public IReadOnlyCollection<(string What, Exception Exception)> Errors => _errors;

        /// <summary>Completes with the first reported error, earlier or later, that matches <paramref name="match"/> (any when null).</summary>
        public async Task<(string What, Exception Exception)> WaitForError(Func<(string What, Exception Exception), bool>? match = null)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (true)
            {
                foreach (var error in _errors)
                {
                    if (match is null || match(error))
                        return error;
                }

                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !await _errorArrived.WaitAsync(left))
                    throw new TimeoutException("No matching error was reported.");
            }
        }

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

        /// <summary>Completes once <paramref name="count"/> more list stats samples have been read.</summary>
        public Task WaitForStatsRecorded(int count)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = 0;
            void Count(string _)
            {
                if (Interlocked.Increment(ref seen) >= count)
                    done.TrySetResult();
            }

            Session.StatsSampleRecorded += Count;
            return done.Task.WaitAsync(Patience).ContinueWith(
                t =>
                {
                    Session.StatsSampleRecorded -= Count;
                    return t;
                },
                TaskScheduler.Default).Unwrap();
        }

        /// <summary>Completes once <paramref name="count"/> more pull progress lines have been read.</summary>
        public Task WaitForPullLines(int count)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = 0;
            void Count(string _)
            {
                if (Interlocked.Increment(ref seen) >= count)
                    done.TrySetResult();
            }

            Session.PullLineRecorded += Count;
            return done.Task.WaitAsync(Patience).ContinueWith(
                t =>
                {
                    Session.PullLineRecorded -= Count;
                    return t;
                },
                TaskScheduler.Default).Unwrap();
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

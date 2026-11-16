using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Session;
using Berth.Tests.Presentation;
using Berth.Text;

namespace Berth.Tests.Session;

public sealed partial class SessionReducerTests
{
    private static readonly EngineVersion Version = new("29.0.0", "1.52", "1.24", "linux", "amd64");
    private static readonly EngineInfo Info = new("host", "29.0.0", 8, 16L << 30, 1, 0, 1, 3);
    private static readonly EngineInfo NewerInfo = new("host", "29.0.0", 8, 16L << 30, 2, 0, 0, 3);

    private static ContainerSummary Container(string id) => new(
        id, [$"/{id}"], "nginx:latest", "sha256:1", "nginx", 1_700_000_000, "running", "Up 1 minute", [],
        new Dictionary<string, string>());

    private static EngineEventMessage Event(string type, string action) =>
        new(type, action, "abc", new Dictionary<string, string>(), 0);

    /// <summary>Runs the reducer, returning the effects as an array so xUnit compares them element by element.</summary>
    private static (EngineState State, SessionEffect[] Effects) Reduce(EngineState state, SessionEvent e)
    {
        var (next, effects) = SessionReducer.Reduce(state, e);
        return (next, effects.ToArray());
    }

    /// <summary>Connected, with containers a and b listed, no pending actions and load generation 1.</summary>
    private static EngineState Connected()
    {
        var (connecting, _) = Reduce(EngineState.Initial, new ConnectRequested());
        var (connected, _) = Reduce(connecting, new ConnectSucceeded(Version, Info));
        var (loaded, _) = Reduce(connected, new ContainersLoaded(connected.LoadGeneration, [Container("a"), Container("b")], Info));
        return loaded;
    }

    [Fact]
    public void Connect_requested_moves_to_connecting_and_emits_Connect()
    {
        var (state, effects) = Reduce(EngineState.Initial, new ConnectRequested());

        Assert.Equal(ConnectionStatus.Connecting, state.Status);
        Assert.Equal(0, state.RetryAttempt);
        Assert.Equal([new ConnectEffect()], effects);
    }

    [Fact]
    public void Connect_requested_while_connected_starts_over()
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected with { RetryAttempt = 3 }, new ConnectRequested());

        Assert.Equal(ConnectionStatus.Connecting, state.Status);
        Assert.Equal(0, state.RetryAttempt);
        Assert.Equal(connected.LoadGeneration + 1, state.LoadGeneration);
        Assert.Equal([new StopStreamsEffect(), new ConnectEffect()], effects);
    }

    [Fact]
    public void Connect_succeeded_loads_containers_and_starts_the_event_stream()
    {
        var (connecting, _) = Reduce(EngineState.Initial, new ConnectRequested());
        connecting = connecting with { RetryAttempt = 4, ConnectionError = "connection refused" };

        var (state, effects) = Reduce(connecting, new ConnectSucceeded(Version, Info));

        Assert.Equal(ConnectionStatus.Connected, state.Status);
        Assert.Same(Version, state.Version);
        Assert.Same(Info, state.Info);
        Assert.Equal(0, state.RetryAttempt);
        Assert.Null(state.ConnectionError);
        Assert.Equal(connecting.LoadGeneration + 1, state.LoadGeneration);
        Assert.Equal([new LoadContainersEffect(state.LoadGeneration), new StartEventStreamEffect()], effects);
    }

    [Fact]
    public void Connect_failed_shows_the_message_and_schedules_a_retry_with_backoff()
    {
        var (connecting, _) = Reduce(EngineState.Initial, new ConnectRequested());

        var (first, firstEffects) = Reduce(connecting, new ConnectFailed("connection refused"));

        Assert.Equal(ConnectionStatus.Disconnected, first.Status);
        Assert.Equal("connection refused", first.ConnectionError);
        Assert.Equal(1, first.RetryAttempt);
        Assert.Equal([new ScheduleRetryEffect(TimeSpan.FromSeconds(1))], firstEffects);

        var (retrying, _) = Reduce(first, new RetryDue());
        var (second, secondEffects) = Reduce(retrying, new ConnectFailed("Engine API 1.40 is too old"));

        Assert.Equal(ConnectionStatus.Disconnected, second.Status);
        Assert.Equal("Engine API 1.40 is too old", second.ConnectionError);
        Assert.Equal(2, second.RetryAttempt);
        Assert.Equal([new ScheduleRetryEffect(TimeSpan.FromSeconds(2))], secondEffects);
    }

    [Fact]
    public void Retry_due_connects_again()
    {
        var (connecting, _) = Reduce(EngineState.Initial, new ConnectRequested());
        var (failed, _) = Reduce(connecting, new ConnectFailed("connection refused"));

        var (state, effects) = Reduce(failed, new RetryDue());

        Assert.Equal(ConnectionStatus.Connecting, state.Status);
        Assert.Equal(1, state.RetryAttempt);
        Assert.Equal("connection refused", state.ConnectionError);
        Assert.Equal([new ConnectEffect()], effects);
    }

    [Fact]
    public void Retry_due_while_already_connected_does_nothing()
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected, new RetryDue());

        Assert.Same(connected, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void Connection_lost_clears_pending_keeps_the_last_list_and_schedules_a_retry()
    {
        var (pending, _) = Reduce(Connected(), new ActionRequested(["a"], ContainerAction.Stop));

        var (state, effects) = Reduce(pending, new ConnectionLost("unexpected EOF"));

        Assert.Equal(ConnectionStatus.Disconnected, state.Status);
        Assert.Equal("unexpected EOF", state.ConnectionError);
        Assert.Empty(state.Pending);
        Assert.Equal(pending.Containers, state.Containers);
        Assert.Equal(2, state.Containers.Length);
        Assert.Equal(1, state.RetryAttempt);
        Assert.Equal(pending.LoadGeneration + 1, state.LoadGeneration);
        Assert.Equal([new StopStreamsEffect(), new ScheduleRetryEffect(TimeSpan.FromSeconds(1))], effects);
    }

    [Fact]
    public void A_relevant_event_schedules_one_reload()
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected, new EngineEventReceived(Event("container", "start")));

        Assert.True(state.ReloadScheduled);
        Assert.Equal(connected.LoadGeneration, state.LoadGeneration);
        Assert.Equal([new ScheduleReloadEffect(SessionReducer.ReloadDebounce)], effects);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("start")]
    [InlineData("die")]
    [InlineData("stop")]
    [InlineData("kill")]
    [InlineData("pause")]
    [InlineData("unpause")]
    [InlineData("restart")]
    [InlineData("rename")]
    [InlineData("destroy")]
    [InlineData("health_status")]
    [InlineData("health_status: healthy")]
    [InlineData("health_status: unhealthy")]
    public void Each_listed_container_action_schedules_a_reload(string action)
    {
        var (state, effects) = Reduce(Connected(), new EngineEventReceived(Event("container", action)));

        Assert.True(state.ReloadScheduled);
        Assert.Equal([new ScheduleReloadEffect(SessionReducer.ReloadDebounce)], effects);
    }

    [Fact]
    public void Twenty_events_before_the_reload_runs_schedule_only_one()
    {
        var state = Connected();
        var all = new List<SessionEffect>();

        for (var i = 0; i < 20; i++)
        {
            (state, var effects) = Reduce(state, new EngineEventReceived(Event("container", i % 2 == 0 ? "start" : "die")));
            all.AddRange(effects);
        }

        Assert.True(state.ReloadScheduled);
        Assert.Equal([new ScheduleReloadEffect(SessionReducer.ReloadDebounce)], all);

        var (afterReload, reloadEffects) = Reduce(state, new ReloadDue());
        Assert.False(afterReload.ReloadScheduled);
        Assert.Equal([new LoadContainersEffect(afterReload.LoadGeneration)], reloadEffects);

        var (next, nextEffects) = Reduce(afterReload, new EngineEventReceived(Event("container", "stop")));
        Assert.True(next.ReloadScheduled);
        Assert.Equal([new ScheduleReloadEffect(SessionReducer.ReloadDebounce)], nextEffects);
    }

    [Theory]
    [InlineData("image", "pull")]
    [InlineData("image", "start")]
    [InlineData("network", "connect")]
    [InlineData("container", "exec_start")]
    [InlineData("container", "exec_create")]
    [InlineData("container", "attach")]
    [InlineData("container", "starting")]
    public void An_irrelevant_event_does_nothing(string type, string action)
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected, new EngineEventReceived(Event(type, action)));

        Assert.Same(connected, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void Reload_due_emits_a_load_with_the_next_generation()
    {
        var (scheduled, _) = Reduce(Connected(), new EngineEventReceived(Event("container", "die")));

        var (state, effects) = Reduce(scheduled, new ReloadDue());

        Assert.False(state.ReloadScheduled);
        Assert.Equal(scheduled.LoadGeneration + 1, state.LoadGeneration);
        Assert.Equal([new LoadContainersEffect(scheduled.LoadGeneration + 1)], effects);
    }

    [Fact]
    public void A_reload_asked_for_loads_the_list_at_once_with_a_new_generation()
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected, new ReloadRequested());

        Assert.Equal(connected.LoadGeneration + 1, state.LoadGeneration);
        Assert.Equal([new LoadContainersEffect(connected.LoadGeneration + 1)], effects);
        Assert.Equal(connected.Containers, state.Containers);
    }

    [Fact]
    public void A_reload_asked_for_while_a_debounced_one_waits_still_loads_and_leaves_the_timer_to_clear_its_flag()
    {
        var (scheduled, _) = Reduce(Connected(), new EngineEventReceived(Event("container", "die")));

        var (state, effects) = Reduce(scheduled, new ReloadRequested());

        Assert.True(state.ReloadScheduled);
        Assert.Equal([new LoadContainersEffect(scheduled.LoadGeneration + 1)], effects);
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.Connecting)]
    public void A_reload_asked_for_while_not_connected_does_nothing(ConnectionStatus status)
    {
        var state = status == ConnectionStatus.Disconnected ? EngineState.Initial : Reduce(EngineState.Initial, new ConnectRequested()).State;

        var (next, effects) = Reduce(state, new ReloadRequested());

        Assert.Same(state, next);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_load_result_from_an_older_generation_is_ignored()
    {
        var connected = Connected();
        var (scheduled, _) = Reduce(connected, new EngineEventReceived(Event("container", "die")));
        var (loading, _) = Reduce(scheduled, new ReloadDue());
        var older = loading.LoadGeneration - 1;

        var (afterLoad, loadEffects) = Reduce(loading, new ContainersLoaded(older, [Container("z")], NewerInfo));
        var (afterFail, failEffects) = Reduce(loading, new LoadFailed(older, "engine is gone", EngineUnreachable: true));

        Assert.Same(loading, afterLoad);
        Assert.Empty(loadEffects);
        Assert.Same(loading, afterFail);
        Assert.Empty(failEffects);

        var (current, currentEffects) = Reduce(loading, new ContainersLoaded(loading.LoadGeneration, [Container("z")], NewerInfo));
        Assert.Equal(["z"], current.Containers.Select(c => c.Id));
        Assert.Same(NewerInfo, current.Info);
        Assert.Empty(currentEffects);
    }

    [Fact]
    public void A_loaded_list_drops_pending_entries_for_containers_that_are_gone()
    {
        var (pending, _) = Reduce(Connected(), new ActionRequested(["a", "b"], ContainerAction.Remove));
        var (scheduled, _) = Reduce(pending, new EngineEventReceived(Event("container", "destroy")));
        var (loading, _) = Reduce(scheduled, new ReloadDue());

        var (state, effects) = Reduce(loading, new ContainersLoaded(loading.LoadGeneration, [Container("b"), Container("c")], NewerInfo));

        Assert.Equal(["b", "c"], state.Containers.Select(c => c.Id));
        Assert.Equal(["b"], state.Pending.Keys);
        Assert.Equal(ContainerAction.Remove, state.Pending["b"]);
        Assert.Same(NewerInfo, state.Info);
        Assert.Empty(effects);
    }

    [Fact]
    public void Action_requested_marks_each_id_pending_and_emits_one_effect_per_id()
    {
        var (state, effects) = Reduce(Connected(), new ActionRequested(["a", "b"], ContainerAction.Stop));

        Assert.Equal(2, state.Pending.Count);
        Assert.Equal(ContainerAction.Stop, state.Pending["a"]);
        Assert.Equal(ContainerAction.Stop, state.Pending["b"]);
        Assert.Equal(
            [new RunActionEffect("a", ContainerAction.Stop, false), new RunActionEffect("b", ContainerAction.Stop, false)],
            effects);
    }

    [Fact]
    public void Action_requested_skips_ids_that_are_already_pending()
    {
        var (pending, _) = Reduce(Connected(), new ActionRequested(["a"], ContainerAction.Stop));

        var (state, effects) = Reduce(pending, new ActionRequested(["a", "b", "b"], ContainerAction.Restart));

        Assert.Equal(ContainerAction.Stop, state.Pending["a"]);
        Assert.Equal(ContainerAction.Restart, state.Pending["b"]);
        Assert.Equal(2, state.Pending.Count);
        Assert.Equal([new RunActionEffect("b", ContainerAction.Restart, false)], effects);
    }

    [Fact]
    public void Action_requested_while_not_connected_does_nothing()
    {
        var (connecting, _) = Reduce(EngineState.Initial, new ConnectRequested());

        var (state, effects) = Reduce(connecting, new ActionRequested(["a"], ContainerAction.Start));

        Assert.Same(connecting, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void Force_is_carried_from_the_request_to_the_effect()
    {
        var (state, effects) = Reduce(Connected(), new ActionRequested(["a"], ContainerAction.Remove, Force: true));

        Assert.Equal(ContainerAction.Remove, state.Pending["a"]);
        Assert.Equal([new RunActionEffect("a", ContainerAction.Remove, true)], effects);
    }

    [Fact]
    public void Action_succeeded_clears_pending_and_schedules_a_reload()
    {
        var (pending, _) = Reduce(Connected(), new ActionRequested(["a", "b"], ContainerAction.Start));

        var (state, effects) = Reduce(pending, new ActionSucceeded("a"));

        Assert.Equal(["b"], state.Pending.Keys);
        Assert.True(state.ReloadScheduled);
        Assert.Null(state.ActionError);
        Assert.Equal([new ScheduleReloadEffect(SessionReducer.ReloadDebounce)], effects);

        var (second, secondEffects) = Reduce(state, new ActionSucceeded("b"));
        Assert.Empty(second.Pending);
        Assert.True(second.ReloadScheduled);
        Assert.Empty(secondEffects);
    }

    [Fact]
    public void Action_failed_clears_pending_shows_the_message_and_schedules_a_reload()
    {
        var (pending, _) = Reduce(Connected(), new ActionRequested(["abc", "b"], ContainerAction.Remove));

        var (state, effects) = Reduce(pending, new ActionFailed("abc", "No such container: abc", EngineUnreachable: false));

        Assert.Equal(ConnectionStatus.Connected, state.Status);
        Assert.Equal(["b"], state.Pending.Keys);
        Assert.Equal("No such container: abc", state.ActionError);
        Assert.True(state.ReloadScheduled);
        Assert.Equal([new ScheduleReloadEffect(SessionReducer.ReloadDebounce)], effects);

        var (later, laterEffects) = Reduce(state, new ActionFailed("b", "You cannot remove a running container", EngineUnreachable: false));
        Assert.Equal("You cannot remove a running container", later.ActionError);
        Assert.Empty(later.Pending);
        Assert.Empty(laterEffects);
    }

    [Fact]
    public void Action_failed_with_engine_unreachable_becomes_connection_lost()
    {
        var (pending, _) = Reduce(Connected(), new ActionRequested(["a", "b"], ContainerAction.Stop));

        var (state, effects) = Reduce(pending, new ActionFailed("a", "connection refused", EngineUnreachable: true));

        Assert.Equal(ConnectionStatus.Disconnected, state.Status);
        Assert.Equal("connection refused", state.ConnectionError);
        Assert.Null(state.ActionError);
        Assert.Empty(state.Pending);
        Assert.Equal(pending.Containers, state.Containers);
        Assert.Equal(1, state.RetryAttempt);
        Assert.Equal(pending.LoadGeneration + 1, state.LoadGeneration);
        Assert.Equal([new StopStreamsEffect(), new ScheduleRetryEffect(TimeSpan.FromSeconds(1))], effects);
    }

    [Fact]
    public void Load_failed_with_engine_unreachable_becomes_connection_lost()
    {
        var (scheduled, _) = Reduce(Connected(), new EngineEventReceived(Event("container", "start")));
        var (loading, _) = Reduce(scheduled, new ReloadDue());

        var (state, effects) = Reduce(loading, new LoadFailed(loading.LoadGeneration, "connection refused", EngineUnreachable: true));

        Assert.Equal(ConnectionStatus.Disconnected, state.Status);
        Assert.Equal("connection refused", state.ConnectionError);
        Assert.Empty(state.Pending);
        Assert.Equal(loading.Containers, state.Containers);
        Assert.Equal(1, state.RetryAttempt);
        Assert.Equal(loading.LoadGeneration + 1, state.LoadGeneration);
        Assert.Equal([new StopStreamsEffect(), new ScheduleRetryEffect(TimeSpan.FromSeconds(1))], effects);
    }

    [Fact]
    public void Load_failed_that_reached_the_engine_shows_the_message_and_keeps_the_list()
    {
        var (scheduled, _) = Reduce(Connected(), new EngineEventReceived(Event("container", "start")));
        var (loading, _) = Reduce(scheduled, new ReloadDue());

        var (state, effects) = Reduce(loading, new LoadFailed(loading.LoadGeneration, "page not found", EngineUnreachable: false));

        Assert.Equal(ConnectionStatus.Connected, state.Status);
        Assert.Equal("page not found", state.ActionError);
        Assert.Equal(loading.Containers, state.Containers);
        Assert.Equal(loading.LoadGeneration, state.LoadGeneration);
        Assert.Empty(effects);
    }

    [Fact]
    public void Dismiss_clears_the_action_error()
    {
        var (pending, _) = Reduce(Connected(), new ActionRequested(["a"], ContainerAction.Pause));
        var (failed, _) = Reduce(pending, new ActionFailed("a", "Container a is not running", EngineUnreachable: false));

        var (state, effects) = Reduce(failed, new ActionErrorDismissed());

        Assert.Null(state.ActionError);
        Assert.Equal(failed with { ActionError = null }, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_failed_clipboard_copy_shows_in_the_error_bar_connected_or_not()
    {
        foreach (var before in new[] { Connected(), EngineState.Initial })
        {
            var (state, effects) = Reduce(before, new ClipboardCopyFailed());

            Assert.Equal("Could not copy to the clipboard. Try again.", state.ActionError);
            Assert.Equal(before with { ActionError = "Could not copy to the clipboard. Try again." }, state);
            Assert.Empty(effects);
        }
    }

    // Copy docker run from the list: the list does not have the container's settings, so the session inspects it first.

    [Theory]
    [InlineData(ShellKind.PowerShell)]
    [InlineData(ShellKind.Posix)]
    public void Copying_the_run_command_inspects_the_container_for_that_shell(ShellKind shell)
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected, new RunCommandRequested("a", shell));

        Assert.Same(connected, state);
        Assert.Equal([new InspectForRunCommandEffect("a", shell)], effects);
    }

    [Fact]
    public void Copying_the_run_command_while_an_action_runs_on_the_container_still_inspects_it()
    {
        var (pending, _) = Reduce(Connected(), new ActionRequested(["a"], ContainerAction.Stop));

        var (state, effects) = Reduce(pending, new RunCommandRequested("a", ShellKind.Posix));

        Assert.Same(pending, state);
        Assert.Equal([new InspectForRunCommandEffect("a", ShellKind.Posix)], effects);
    }

    [Theory]
    [InlineData(ShellKind.PowerShell)]
    [InlineData(ShellKind.Posix)]
    public void An_inspected_container_has_its_run_command_copied(ShellKind shell)
    {
        var connected = Connected();
        var details = DetailsFixtures.Details("a") with { Env = ["GREETING=it's here"] };

        var (state, effects) = Reduce(connected, new RunCommandInspected("a", shell, details));

        Assert.Same(connected, state);
        Assert.Equal([new CopyTextEffect(DockerRunCommand.Build(details, shell))], effects);
    }

    [Fact]
    public void A_failed_inspect_for_the_run_command_shows_the_engine_message_as_is()
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected, new RunCommandFailed("a", "No such container: a", EngineUnreachable: false));

        Assert.Equal(connected with { ActionError = "No such container: a" }, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_failed_inspect_for_the_run_command_that_cannot_reach_the_engine_becomes_connection_lost()
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected, new RunCommandFailed("a", "connection refused", EngineUnreachable: true));

        Assert.Equal(ConnectionStatus.Disconnected, state.Status);
        Assert.Equal("connection refused", state.ConnectionError);
        Assert.Null(state.ActionError);
        Assert.Equal([new StopStreamsEffect(), new ScheduleRetryEffect(TimeSpan.FromSeconds(1))], effects);
    }

    [Fact]
    public void Events_other_than_connect_are_ignored_while_disconnected()
    {
        var disconnected = EngineState.Initial;
        SessionEvent[] events =
        [
            new ConnectSucceeded(Version, Info),
            new ConnectFailed("connection refused"),
            new ConnectionLost("unexpected EOF"),
            new ContainersLoaded(disconnected.LoadGeneration, [Container("a")], Info),
            new LoadFailed(disconnected.LoadGeneration, "connection refused", EngineUnreachable: true),
            new LoadFailed(disconnected.LoadGeneration, "page not found", EngineUnreachable: false),
            new EngineEventReceived(Event("container", "start")),
            new ReloadDue(),
            new ActionRequested(["a"], ContainerAction.Start),
            new ActionSucceeded("a"),
            new ActionFailed("a", "No such container: a", EngineUnreachable: false),
            new ActionFailed("a", "connection refused", EngineUnreachable: true),
            new RunCommandRequested("a", ShellKind.PowerShell),
            new RunCommandInspected("a", ShellKind.PowerShell, DetailsFixtures.Details("a")),
            new RunCommandFailed("a", "No such container: a", EngineUnreachable: false),
            new RunCommandFailed("a", "connection refused", EngineUnreachable: true),
        ];

        foreach (var e in events)
        {
            var (state, effects) = Reduce(disconnected, e);

            Assert.Same(disconnected, state);
            Assert.Empty(effects);
        }

        var (connecting, connectEffects) = Reduce(disconnected, new ConnectRequested());
        Assert.Equal(ConnectionStatus.Connecting, connecting.Status);
        Assert.Equal([new ConnectEffect()], connectEffects);
    }

    [Fact]
    public void Connect_requested_while_connecting_starts_over()
    {
        var (connecting, _) = Reduce(EngineState.Initial, new ConnectRequested());

        var (state, effects) = Reduce(connecting with { RetryAttempt = 2 }, new ConnectRequested());

        Assert.Equal(ConnectionStatus.Connecting, state.Status);
        Assert.Equal(0, state.RetryAttempt);
        Assert.Equal(connecting.LoadGeneration + 1, state.LoadGeneration);
        Assert.Equal([new StopStreamsEffect(), new ConnectEffect()], effects);
    }

    [Fact]
    public void A_manual_reconnect_does_not_leave_an_action_pending_forever()
    {
        var (pending, _) = Reduce(Connected(), new ActionRequested(["a"], ContainerAction.Stop));
        var (restarting, _) = Reduce(pending, new ConnectRequested());
        var (afterResult, resultEffects) = Reduce(restarting, new ActionSucceeded("a"));
        var (reconnected, _) = Reduce(afterResult, new ConnectSucceeded(Version, Info));

        var (state, effects) = Reduce(reconnected, new ContainersLoaded(reconnected.LoadGeneration, [Container("a"), Container("b")], Info));

        Assert.Empty(restarting.Pending);
        Assert.Empty(resultEffects);
        Assert.Empty(state.Pending);
        Assert.Equal(["a", "b"], state.Containers.Select(c => c.Id));
        Assert.Empty(effects);
    }

    [Fact]
    public void Reconnecting_after_a_lost_connection_with_a_reload_scheduled_still_reloads_on_the_next_event()
    {
        var (scheduled, _) = Reduce(Connected(), new EngineEventReceived(Event("container", "start")));
        var (lost, _) = Reduce(scheduled, new ConnectionLost("unexpected EOF"));
        var (retrying, _) = Reduce(lost, new RetryDue());
        var (reconnected, _) = Reduce(retrying, new ConnectSucceeded(Version, Info));

        var (state, effects) = Reduce(reconnected, new EngineEventReceived(Event("container", "die")));

        Assert.False(lost.ReloadScheduled);
        Assert.True(state.ReloadScheduled);
        Assert.Equal([new ScheduleReloadEffect(SessionReducer.ReloadDebounce)], effects);
    }

    [Fact]
    public void Starting_over_with_a_reload_scheduled_still_reloads_on_the_next_event()
    {
        var (scheduled, _) = Reduce(Connected(), new EngineEventReceived(Event("container", "start")));
        var (restarting, _) = Reduce(scheduled, new ConnectRequested());
        var (reconnected, _) = Reduce(restarting, new ConnectSucceeded(Version, Info));

        var (state, effects) = Reduce(reconnected, new EngineEventReceived(Event("container", "die")));

        Assert.False(restarting.ReloadScheduled);
        Assert.True(state.ReloadScheduled);
        Assert.Equal([new ScheduleReloadEffect(SessionReducer.ReloadDebounce)], effects);
    }

    [Fact]
    public void Reload_due_while_disconnected_clears_the_flag_and_emits_nothing()
    {
        var disconnected = EngineState.Initial with { ReloadScheduled = true };

        var (state, effects) = Reduce(disconnected, new ReloadDue());

        Assert.Equal(disconnected with { ReloadScheduled = false }, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void Connection_lost_while_connecting_is_ignored()
    {
        var (connecting, _) = Reduce(Connected(), new ConnectRequested());

        var (state, effects) = Reduce(connecting, new ConnectionLost("stream closed"));

        Assert.Same(connecting, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void The_list_is_not_loaded_at_first_nor_right_after_a_connect()
    {
        var (connecting, _) = Reduce(EngineState.Initial, new ConnectRequested());
        var (connected, _) = Reduce(connecting, new ConnectSucceeded(Version, Info));

        Assert.False(EngineState.Initial.ListLoaded);
        Assert.False(connected.ListLoaded);
    }

    [Fact]
    public void The_first_list_of_a_connection_marks_it_loaded()
    {
        var (connecting, _) = Reduce(EngineState.Initial, new ConnectRequested());
        var (connected, _) = Reduce(connecting, new ConnectSucceeded(Version, Info));

        var (empty, _) = Reduce(connected, new ContainersLoaded(connected.LoadGeneration, [], Info));

        Assert.True(empty.ListLoaded);
        Assert.True(Connected().ListLoaded);
    }

    [Fact]
    public void A_list_from_an_older_load_does_not_mark_it_loaded()
    {
        var (connecting, _) = Reduce(EngineState.Initial, new ConnectRequested());
        var (connected, _) = Reduce(connecting, new ConnectSucceeded(Version, Info));

        var (state, _) = Reduce(connected, new ContainersLoaded(connected.LoadGeneration - 1, [Container("a")], Info));

        Assert.False(state.ListLoaded);
    }

    [Fact]
    public void Starting_over_clears_the_loaded_mark_until_the_next_list()
    {
        var (connecting, _) = Reduce(Connected(), new ConnectRequested());
        Assert.False(connecting.ListLoaded);

        var (connected, _) = Reduce(connecting, new ConnectSucceeded(Version, Info));
        Assert.False(connected.ListLoaded);

        var (loaded, _) = Reduce(connected, new ContainersLoaded(connected.LoadGeneration, [Container("a")], Info));
        Assert.True(loaded.ListLoaded);
    }

    [Fact]
    public void Reconnecting_after_a_lost_connection_clears_the_loaded_mark_until_the_next_list()
    {
        var (lost, _) = Reduce(Connected(), new ConnectionLost("stream closed"));
        var (retrying, _) = Reduce(lost, new RetryDue());

        var (connected, _) = Reduce(retrying, new ConnectSucceeded(Version, Info));

        Assert.False(connected.ListLoaded);
        Assert.Equal(["a", "b"], connected.Containers.Select(c => c.Id));
    }

    private static ContainerSummary Stopped(string id) => Container(id) with { State = "exited", Status = "Exited (0) 1 minute ago" };

    private static StatsSample Sample(double cpu) =>
        new(new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero), cpu, 1024, 4096, 0, 0, 0, 0, 1);

    private static ImmutableDictionary<string, StatsSample> Batch(params (string Id, double Cpu)[] samples) =>
        samples.ToImmutableDictionary(s => s.Id, s => Sample(s.Cpu), StringComparer.Ordinal);

    /// <summary>Connected with a and b running and c stopped, list stats wanted.</summary>
    private static EngineState Watching()
    {
        var (connecting, _) = Reduce(EngineState.Initial, new ConnectRequested());
        var (connected, _) = Reduce(connecting, new ConnectSucceeded(Version, Info));
        var (loaded, _) = Reduce(
            connected, new ContainersLoaded(connected.LoadGeneration, [Container("a"), Container("b"), Stopped("c")], Info));
        var (watching, _) = Reduce(loaded, new StatsWantedChanged(true));
        return watching;
    }

    [Fact]
    public void Stats_wanted_while_connected_watches_the_running_containers()
    {
        var (connecting, _) = Reduce(EngineState.Initial, new ConnectRequested());
        var (connected, _) = Reduce(connecting, new ConnectSucceeded(Version, Info));
        var (loaded, _) = Reduce(
            connected, new ContainersLoaded(connected.LoadGeneration, [Container("a"), Container("b"), Stopped("c")], Info));

        var (state, effects) = Reduce(loaded, new StatsWantedChanged(true));

        Assert.True(state.StatsWanted);
        Assert.Equal([new WatchStatsEffect(["a", "b"])], effects);
    }

    [Fact]
    public void Stats_wanted_again_changes_nothing()
    {
        var watching = Watching();

        var (state, effects) = Reduce(watching, new StatsWantedChanged(true));

        Assert.Same(watching, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void Stats_wanted_before_the_list_is_loaded_waits_for_the_list()
    {
        var (connecting, _) = Reduce(EngineState.Initial, new ConnectRequested());
        var (wanted, wantedEffects) = Reduce(connecting, new StatsWantedChanged(true));
        var (connected, connectedEffects) = Reduce(wanted, new ConnectSucceeded(Version, Info));

        var (state, effects) = Reduce(connected, new ContainersLoaded(connected.LoadGeneration, [Container("a")], Info));

        Assert.True(wanted.StatsWanted);
        Assert.Empty(wantedEffects);
        Assert.DoesNotContain(connectedEffects, e => e is WatchStatsEffect);
        Assert.Equal([new WatchStatsEffect(["a"])], effects);
        Assert.True(state.StatsWanted);
    }

    [Fact]
    public void A_list_change_re_emits_the_watch_and_drops_the_stats_of_containers_no_longer_running()
    {
        var (sampled, _) = Reduce(Watching(), new StatsBatchArrived(Batch(("a", 1), ("b", 2))));

        var (state, effects) = Reduce(
            sampled, new ContainersLoaded(sampled.LoadGeneration, [Stopped("a"), Container("b"), Container("c")], Info));

        Assert.Equal([new WatchStatsEffect(["b", "c"])], effects);
        Assert.Equal(["b"], state.Stats.Keys);
    }

    [Fact]
    public void A_list_while_stats_are_not_wanted_watches_nothing()
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected, new ContainersLoaded(connected.LoadGeneration, [Container("a")], Info));

        Assert.Empty(effects);
        Assert.Empty(state.Stats);
    }

    [Fact]
    public void Stats_not_wanted_stops_the_watch_and_clears_the_stats()
    {
        var (sampled, _) = Reduce(Watching(), new StatsBatchArrived(Batch(("a", 1))));

        var (state, effects) = Reduce(sampled, new StatsWantedChanged(false));

        Assert.False(state.StatsWanted);
        Assert.Empty(state.Stats);
        Assert.Equal([new WatchStatsEffect([])], effects);
    }

    [Fact]
    public void A_lost_connection_clears_the_stats_and_keeps_them_wanted()
    {
        var (sampled, _) = Reduce(Watching(), new StatsBatchArrived(Batch(("a", 1))));

        var (state, effects) = Reduce(sampled, new ConnectionLost("stream closed"));

        Assert.True(state.StatsWanted);
        Assert.Empty(state.Stats);
        Assert.Contains(new StopStreamsEffect(), effects);
    }

    [Fact]
    public void Starting_over_clears_the_stats()
    {
        var (sampled, _) = Reduce(Watching(), new StatsBatchArrived(Batch(("a", 1))));

        var (state, effects) = Reduce(sampled, new ConnectRequested());

        Assert.Empty(state.Stats);
        Assert.Equal([new StopStreamsEffect(), new ConnectEffect()], effects);
    }

    [Fact]
    public void A_batch_replaces_the_latest_and_ignores_ids_not_running()
    {
        var (first, _) = Reduce(Watching(), new StatsBatchArrived(Batch(("a", 1), ("b", 2))));

        var (state, effects) = Reduce(first, new StatsBatchArrived(Batch(("a", 5), ("c", 7), ("gone", 9))));

        Assert.Empty(effects);
        Assert.Equal(["a", "b"], state.Stats.Keys.Order());
        Assert.Equal(5, state.Stats["a"].CpuPercent);
        Assert.Equal(2, state.Stats["b"].CpuPercent);
    }

    [Fact]
    public void A_batch_when_stats_are_not_wanted_is_ignored()
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected, new StatsBatchArrived(Batch(("a", 1))));

        Assert.Same(connected, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void Watch_effects_compare_by_their_ids() =>
        Assert.Equal(new WatchStatsEffect(["a", "b"]), new WatchStatsEffect(ImmutableArray.Create("a", "b")));
}

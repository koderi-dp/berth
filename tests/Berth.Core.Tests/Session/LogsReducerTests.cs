using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Session;
using Berth.State;

namespace Berth.Tests.Session;

public sealed class LogsReducerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    internal static ContainerSummary Container(
        string id, string state = "running", long created = 1_700_000_000, string? project = null, string? service = null)
    {
        var labels = new Dictionary<string, string>();
        if (project is not null)
            labels["com.docker.compose.project"] = project;
        if (service is not null)
            labels["com.docker.compose.service"] = service;
        return new ContainerSummary(id, [$"/{id}-name"], "nginx", "sha256:1", "nginx", created, state, "Up", [], labels);
    }

    private static LogLine Line(string text, DateTimeOffset? time, LogStream stream = LogStream.Stdout) => new(stream, time, text);

    private static (LogsState State, ImmutableArray<LogsEffect> Effects) Reduce(LogsState state, LogsEvent e) => LogsReducer.Reduce(state, e);

    private static (LogsState State, ImmutableArray<LogsEffect> Effects) List(LogsState state, params ContainerSummary[] containers) =>
        Reduce(state, new LogsContainersChanged([.. containers]));

    private static LogsState Lines(LogsState state, params IncomingLogLine[] lines) =>
        Reduce(state, new LogsLinesArrived([.. lines], T0.AddHours(1))).State;

    private static int GenerationOf(LogsState state, string id) => state.Sources[id].Generation;

    private static IncomingLogLine From(LogsState state, string id, string text, DateTimeOffset? time, LogStream stream = LogStream.Stdout) =>
        new(id, GenerationOf(state, id), Line(text, time, stream));

    [Fact]
    public void Each_running_container_gets_one_stream_of_its_last_100_lines_then_new_ones()
    {
        var (state, effects) = List(LogsState.Initial, Container("a"), Container("b", state: "paused"), Container("c", state: "exited"));

        var follows = effects.OfType<FollowContainerLogEffect>().ToList();
        Assert.Equal(["a", "b"], follows.Select(f => f.ContainerId).Order());
        Assert.All(follows, f => Assert.Equal(new LogRequest(LogsReducer.Tail, Follow: true), f.Request));
        Assert.Equal(LogSourceStatus.Live, state.Sources["a"].Status);
        Assert.False(state.Sources.ContainsKey("c"));
        Assert.True(state.ContainersKnown);
        Assert.True(state.EngineHasContainers);
        Assert.Equal(0, state.NotFollowed);
    }

    [Fact]
    public void A_list_that_changes_nothing_starts_nothing_again()
    {
        var (state, _) = List(LogsState.Initial, Container("a"));
        var (again, effects) = List(state, Container("a"));

        Assert.Empty(effects);
        Assert.Equal(state.Sources["a"].Generation, again.Sources["a"].Generation);
    }

    [Fact]
    public void A_container_that_starts_later_joins()
    {
        var (state, _) = List(LogsState.Initial, Container("a"), Container("b", state: "created"));
        var (next, effects) = List(state, Container("a"), Container("b"));

        var follow = Assert.Single(effects.OfType<FollowContainerLogEffect>());
        Assert.Equal("b", follow.ContainerId);
        Assert.Equal(LogSourceStatus.Live, next.Sources["b"].Status);
    }

    [Fact]
    public void Lines_carry_their_container_compose_project_and_service()
    {
        var (state, _) = List(LogsState.Initial, Container("a", project: "shop", service: "web"), Container("b"));
        state = Lines(state, From(state, "a", "hello", T0), From(state, "b", "oops", T0.AddSeconds(1), LogStream.Stderr));

        var entries = state.Log.Entries;
        Assert.Equal(new LogOrigin("a", "a-name", "shop", "web"), entries[0].Origin);
        Assert.Equal("shop/web", entries[0].Origin.Label);
        Assert.Equal("b-name", entries[1].Origin.Label);
        Assert.Equal(LogStream.Stderr, entries[1].Line.Stream);
        Assert.Equal(T0, entries[0].Time);
    }

    [Fact]
    public void Lines_are_merged_by_time_and_lines_within_one_second_keep_their_arrival_order()
    {
        var (state, _) = List(LogsState.Initial, Container("a"), Container("b"));

        // a's tail arrives first, then b's, older and newer lines mixed.
        state = Lines(state, From(state, "a", "a1", T0.AddSeconds(1.5)), From(state, "a", "a3", T0.AddSeconds(3)));
        state = Lines(
            state,
            From(state, "b", "b0", T0),
            From(state, "b", "b1", T0.AddSeconds(1.2)),
            From(state, "b", "b2", T0.AddSeconds(2)));

        // Within second 1, a1 (arrived first) stays before b1, though b1 is earlier within that second.
        Assert.Equal(["b0", "a1", "b1", "b2", "a3"], state.Log.Entries.Select(e => e.Line.Text));
    }

    [Fact]
    public void A_line_without_a_timestamp_takes_the_time_it_arrived()
    {
        var (state, _) = List(LogsState.Initial, Container("a"));
        state = Lines(state, From(state, "a", "no time", null));

        Assert.Equal(T0.AddHours(1), Assert.Single(state.Log.Entries).Time);
    }

    [Fact]
    public void Lines_of_an_older_stream_are_ignored()
    {
        var (state, _) = List(LogsState.Initial, Container("a"));
        var old = GenerationOf(state, "a");

        var next = Lines(state, new IncomingLogLine("a", old + 99, Line("stale", T0)));

        Assert.Same(state, next);
    }

    [Fact]
    public void A_stream_that_ends_keeps_its_lines_and_is_not_started_again_by_its_end()
    {
        var (state, _) = List(LogsState.Initial, Container("a"));
        state = Lines(state, From(state, "a", "last", T0));

        var (ended, effects) = Reduce(state, new LogsStreamEnded("a", GenerationOf(state, "a"), Error: null));

        Assert.Empty(effects);
        Assert.Equal(LogSourceStatus.Ended, ended.Sources["a"].Status);
        Assert.Single(ended.Log.Entries);
    }

    [Fact]
    public void A_container_that_runs_again_is_followed_from_its_newest_line_shown_without_repeating_it()
    {
        var (state, _) = List(LogsState.Initial, Container("a"));
        state = Lines(state, From(state, "a", "before", T0));
        state = Reduce(state, new LogsStreamEnded("a", GenerationOf(state, "a"), Error: null)).State;
        state = List(state, Container("a", state: "exited")).State;

        var (restarted, effects) = List(state, Container("a"));

        var follow = Assert.Single(effects.OfType<FollowContainerLogEffect>());
        Assert.Equal(new LogRequest(Tail: -1, Follow: true) { Since = T0 }, follow.Request);

        restarted = Lines(restarted, From(restarted, "a", "before", T0), From(restarted, "a", "after", T0.AddSeconds(5)));
        Assert.Equal(["before", "after"], restarted.Log.Entries.Select(e => e.Line.Text));
    }

    [Fact]
    public void A_failed_stream_says_why()
    {
        var (state, _) = List(LogsState.Initial, Container("a"));

        var (failed, _) = Reduce(state, new LogsStreamEnded("a", GenerationOf(state, "a"), "logging driver does not support reading"));

        Assert.Equal(LogSourceStatus.Failed, failed.Sources["a"].Status);
        Assert.Equal("logging driver does not support reading", failed.Sources["a"].Error);
    }

    [Fact]
    public void A_removed_container_keeps_its_lines_marked_removed_and_its_stream_is_stopped()
    {
        var (state, _) = List(LogsState.Initial, Container("a"), Container("b"));
        state = Lines(state, From(state, "a", "kept", T0));
        var generation = GenerationOf(state, "a");

        var (next, effects) = List(state, Container("b"));

        Assert.Equal(new StopContainerLogEffect("a", generation), Assert.Single(effects));
        Assert.True(next.Sources["a"].Removed);
        Assert.False(next.Sources["a"].Running);
        Assert.Equal("kept", Assert.Single(next.Log.Entries).Line.Text);
    }

    [Fact]
    public void At_most_50_streams_run_the_newest_containers_first_and_the_rest_are_counted()
    {
        var containers = Enumerable.Range(0, 60).Select(i => Container($"c{i:D2}", created: 1_700_000_000 + i)).ToArray();

        var (state, effects) = List(LogsState.Initial, containers);

        var followed = effects.OfType<FollowContainerLogEffect>().Select(f => f.ContainerId).ToHashSet();
        Assert.Equal(LogsReducer.MaxStreams, followed.Count);
        Assert.Equal(10, state.NotFollowed);
        Assert.Contains("c59", followed);
        Assert.DoesNotContain("c00", followed);
        Assert.Equal(LogSourceStatus.Idle, state.Sources["c00"].Status);
    }

    [Fact]
    public void A_stream_that_ends_hands_its_place_to_a_container_waiting_for_one()
    {
        var containers = Enumerable.Range(0, 51).Select(i => Container($"c{i:D2}", created: 1_700_000_000 + i)).ToArray();
        var (state, _) = List(LogsState.Initial, containers);
        Assert.Equal(1, state.NotFollowed);

        var (next, effects) = Reduce(state, new LogsStreamEnded("c50", GenerationOf(state, "c50"), Error: null));

        Assert.Equal("c00", Assert.Single(effects.OfType<FollowContainerLogEffect>()).ContainerId);
        Assert.Equal(0, next.NotFollowed);
    }

    [Fact]
    public void An_engine_without_containers_is_known_to_have_none()
    {
        var (state, effects) = List(LogsState.Initial);

        Assert.Empty(effects);
        Assert.True(state.ContainersKnown);
        Assert.False(state.EngineHasContainers);
    }

    [Fact]
    public void After_the_connection_is_lost_nothing_more_is_taken_in()
    {
        var (state, _) = List(LogsState.Initial, Container("a"));
        var generation = GenerationOf(state, "a");

        var (lost, _) = Reduce(state, new LogsConnectionLost());
        var (later, effects) = List(lost, Container("a"), Container("b"));
        var withLines = Lines(later, new IncomingLogLine("a", generation, Line("late", T0)));

        Assert.True(lost.ConnectionLost);
        Assert.Equal(LogSourceStatus.Ended, lost.Sources["a"].Status);
        Assert.Empty(effects);
        Assert.Same(lost, later);
        Assert.Same(lost, withLines);
    }

    [Fact]
    public void The_log_keeps_its_cap_across_containers()
    {
        var (state, _) = List(LogsState.Initial, Container("a"), Container("b"));
        var lines = Enumerable.Range(0, MergedLog.Capacity + 10)
            .Select(i => From(state, i % 2 == 0 ? "a" : "b", $"line {i}", T0.AddSeconds(i)))
            .ToArray();

        state = Lines(state, lines);

        Assert.Equal(MergedLog.Capacity, state.Log.Entries.Count);
        Assert.Equal(10, state.Log.Dropped);
        Assert.Equal("line 10", state.Log.Entries[0].Line.Text);
    }
}

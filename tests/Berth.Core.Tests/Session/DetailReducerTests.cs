using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;

namespace Berth.Tests.Session;

public sealed class DetailReducerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);

    private static LogLine Line(string text) => new(LogStream.Stdout, null, text);

    private static StatsSample Sample(int second, double cpu) =>
        new(T0.AddSeconds(second), cpu, 1024, 4096, 0, 0, 0, 0, 1);

    /// <summary>Runs the reducer, returning the effects as an array so xUnit compares them element by element.</summary>
    private static (DetailState State, DetailEffect[] Effects) Reduce(DetailState state, DetailEvent e)
    {
        var (next, effects) = DetailReducer.Reduce(state, e);
        return (next, effects.ToArray());
    }

    /// <summary>The same inspection, as if the container had been started again a minute later.</summary>
    private static ContainerInspection Restarted(ContainerInspection inspection) => inspection with
    {
        Details = inspection.Details with
        {
            State = inspection.Details.State with { StartedAt = inspection.Details.State.StartedAt!.Value.AddMinutes(1) },
        },
    };

    /// <summary>Opened on container c, read as <paramref name="running"/>, with its streams started (generation 1).</summary>
    private static DetailState Live(bool running = true)
    {
        var (opened, _) = Reduce(DetailState.Initial("c"), new DetailOpenRequested());
        var (loaded, _) = Reduce(opened, new InspectLoaded(opened.InspectGeneration, FakeEngineApi.Inspection("c", running)));
        return loaded;
    }

    [Fact]
    public void Open_loads_inspect()
    {
        var (state, effects) = Reduce(DetailState.Initial("c"), new DetailOpenRequested());

        Assert.Equal(1, state.InspectGeneration);
        Assert.Equal(StreamStatus.Idle, state.LogStatus);
        Assert.Equal([new InspectContainerEffect(1)], effects);
    }

    [Fact]
    public void Opening_twice_reads_once()
    {
        var (opened, _) = Reduce(DetailState.Initial("c"), new DetailOpenRequested());

        var (state, effects) = Reduce(opened, new DetailOpenRequested());

        Assert.Same(opened, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void Inspect_loaded_starts_logs_and_stats_for_a_running_container()
    {
        var (opened, _) = Reduce(DetailState.Initial("c"), new DetailOpenRequested());
        var inspection = FakeEngineApi.Inspection("c", running: true, tty: true);

        var (state, effects) = Reduce(opened, new InspectLoaded(opened.InspectGeneration, inspection));

        Assert.Same(inspection.Details, state.Details);
        Assert.Equal(inspection.RawJson, state.RawJson);
        Assert.Equal(StreamStatus.Loading, state.LogStatus);
        Assert.Equal(StreamStatus.Loading, state.StatsStatus);
        Assert.Equal(1, state.StreamGeneration);
        Assert.Equal(
            [new FollowLogsEffect(1, Tty: true, new LogRequest(1000, Follow: true)), new FollowStatsEffect(1)],
            effects);
    }

    [Fact]
    public void A_stopped_container_gets_logs_and_no_stats()
    {
        var (opened, _) = Reduce(DetailState.Initial("c"), new DetailOpenRequested());

        var (state, effects) = Reduce(
            opened, new InspectLoaded(opened.InspectGeneration, FakeEngineApi.Inspection("c", running: false)));

        Assert.Equal(StreamStatus.Loading, state.LogStatus);
        Assert.Equal(StreamStatus.Idle, state.StatsStatus);
        Assert.Equal([new FollowLogsEffect(1, Tty: false, DetailReducer.LogsRequest)], effects);
    }

    [Fact]
    public void Log_batches_append()
    {
        var live = Live();

        var (first, _) = Reduce(live, new LogLinesArrived(1, [Line("one"), Line("two")]));
        var (state, effects) = Reduce(first, new LogLinesArrived(1, [Line("three")]));

        Assert.Equal(["one", "two", "three"], state.Logs.Lines.Select(l => l.Text));
        Assert.Equal(StreamStatus.Live, state.LogStatus);
        Assert.Empty(effects);
    }

    [Fact]
    public void The_log_stream_ending_keeps_the_lines()
    {
        var (withLines, _) = Reduce(Live(running: false), new LogLinesArrived(1, [Line("one")]));

        var (state, effects) = Reduce(withLines, new LogStreamEnded(1, Error: null));

        Assert.Equal(StreamStatus.Ended, state.LogStatus);
        Assert.Null(state.LogError);
        Assert.Equal(["one"], state.Logs.Lines.Select(l => l.Text));
        Assert.Empty(effects);
    }

    [Fact]
    public void The_streams_of_a_running_container_ending_read_it_again()
    {
        var live = Live();

        var (state, effects) = Reduce(live, new StatsStreamEnded(1, Error: null));

        Assert.Equal(StreamStatus.Ended, state.StatsStatus);
        Assert.Equal([new InspectContainerEffect(live.InspectGeneration + 1)], effects);
    }

    [Fact]
    public void A_log_failure_keeps_the_lines_and_shows_the_message()
    {
        var (withLines, _) = Reduce(Live(), new LogLinesArrived(1, [Line("one")]));

        var (state, effects) = Reduce(withLines, new LogStreamEnded(1, "unexpected EOF"));

        Assert.Equal(StreamStatus.Failed, state.LogStatus);
        Assert.Equal("unexpected EOF", state.LogError);
        Assert.Equal(["one"], state.Logs.Lines.Select(l => l.Text));
        Assert.Empty(effects);
    }

    [Fact]
    public void Clear_empties_the_view()
    {
        var (withLines, _) = Reduce(Live(), new LogLinesArrived(1, [Line("one"), Line("two")]));

        var (cleared, effects) = Reduce(withLines, new LogsClearRequested());
        var (state, _) = Reduce(cleared, new LogLinesArrived(1, [Line("three")]));

        Assert.Empty(cleared.Logs.Lines);
        Assert.Empty(effects);
        Assert.Equal(StreamStatus.Live, cleared.LogStatus);
        Assert.Equal(["three"], state.Logs.Lines.Select(l => l.Text));
        Assert.Equal(3, state.Logs.FirstNumber);
    }

    [Fact]
    public void Stats_samples_build_the_history()
    {
        var live = Live();

        var (first, _) = Reduce(live, new StatsSampleArrived(1, Sample(0, cpu: 1.5)));
        var (state, effects) = Reduce(first, new StatsSampleArrived(1, Sample(1, cpu: 12.5)));

        Assert.Equal(StreamStatus.Live, state.StatsStatus);
        Assert.Equal([1.5, 12.5], state.Stats.Cpu.Select(p => p.Value));
        Assert.Equal(12.5, state.Stats.Latest?.CpuPercent);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_stats_failure_shows_the_message()
    {
        var (state, effects) = Reduce(Live(), new StatsStreamEnded(1, "stats broke"));

        Assert.Equal(StreamStatus.Failed, state.StatsStatus);
        Assert.Equal("stats broke", state.StatsError);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_404_marks_the_container_removed()
    {
        var live = Live();
        var (refreshing, _) = Reduce(live, new DetailRefreshRequested());

        var (state, effects) = Reduce(refreshing, new InspectFailed(refreshing.InspectGeneration, "No such container: c", NotFound: true));

        Assert.True(state.Removed);
        Assert.Null(state.LoadError);
        Assert.Equal(StreamStatus.Ended, state.LogStatus);
        Assert.Equal(StreamStatus.Ended, state.StatsStatus);
        Assert.Equal([new StopDetailStreamsEffect()], effects);
    }

    [Fact]
    public void Another_inspect_failure_shows_the_message_and_keeps_the_details()
    {
        var live = Live();
        var (refreshing, _) = Reduce(live, new DetailRefreshRequested());

        var (state, effects) = Reduce(refreshing, new InspectFailed(refreshing.InspectGeneration, "server error", NotFound: false));

        Assert.False(state.Removed);
        Assert.Equal("server error", state.LoadError);
        Assert.Same(live.Details, state.Details);
        Assert.Empty(effects);
    }

    [Fact]
    public void Refresh_without_a_restart_leaves_the_streams_alone()
    {
        var (withLines, _) = Reduce(Live(), new LogLinesArrived(1, [Line("one")]));
        var (refreshing, refreshEffects) = Reduce(withLines, new DetailRefreshRequested());

        var (state, effects) = Reduce(
            refreshing, new InspectLoaded(refreshing.InspectGeneration, FakeEngineApi.Inspection("c")));

        Assert.Equal([new InspectContainerEffect(2)], refreshEffects);
        Assert.Equal(1, state.StreamGeneration);
        Assert.Equal(["one"], state.Logs.Lines.Select(l => l.Text));
        Assert.Empty(effects);
    }

    [Fact]
    public void Refresh_after_a_restart_restarts_the_streams()
    {
        var (withLines, _) = Reduce(Live(), new LogLinesArrived(1, [Line("one")]));
        var (withSample, _) = Reduce(withLines, new StatsSampleArrived(1, Sample(0, cpu: 1.5)));
        var (refreshing, _) = Reduce(withSample, new DetailRefreshRequested());

        var (state, effects) = Reduce(
            refreshing, new InspectLoaded(refreshing.InspectGeneration, Restarted(FakeEngineApi.Inspection("c"))));

        Assert.Equal(2, state.StreamGeneration);
        Assert.Empty(state.Logs.Lines);
        Assert.Equal(StreamStatus.Loading, state.LogStatus);
        Assert.Equal(StreamStatus.Loading, state.StatsStatus);
        Assert.Same(withSample.Stats, state.Stats);
        Assert.Equal(
            [new StopDetailStreamsEffect(), new FollowLogsEffect(2, Tty: false, DetailReducer.LogsRequest), new FollowStatsEffect(2)],
            effects);
    }

    [Fact]
    public void Results_for_an_older_stream_are_ignored()
    {
        var (refreshing, _) = Reduce(Live(), new DetailRefreshRequested());
        var (restarted, _) = Reduce(
            refreshing, new InspectLoaded(refreshing.InspectGeneration, Restarted(FakeEngineApi.Inspection("c"))));

        DetailEvent[] stale =
        [
            new LogLinesArrived(1, [Line("old")]),
            new LogStreamEnded(1, "cancelled"),
            new StatsSampleArrived(1, Sample(0, cpu: 1)),
            new StatsStreamEnded(1, null),
            new InspectLoaded(1, FakeEngineApi.Inspection("c", running: false)),
            new InspectFailed(1, "No such container: c", NotFound: true),
        ];
        foreach (var e in stale)
        {
            var (state, effects) = Reduce(restarted, e);
            Assert.Same(restarted, state);
            Assert.Empty(effects);
        }
    }

    [Fact]
    public void A_lost_connection_ends_the_streams_and_ignores_what_follows()
    {
        var (withLines, _) = Reduce(Live(), new LogLinesArrived(1, [Line("one")]));

        var (lost, effects) = Reduce(withLines, new DetailConnectionLost());
        var (afterLines, _) = Reduce(lost, new LogLinesArrived(1, [Line("late")]));
        var (afterRefresh, refreshEffects) = Reduce(lost, new DetailRefreshRequested());

        Assert.True(lost.ConnectionLost);
        Assert.Equal(StreamStatus.Ended, lost.LogStatus);
        Assert.Equal(StreamStatus.Ended, lost.StatsStatus);
        Assert.Equal(["one"], lost.Logs.Lines.Select(l => l.Text));
        Assert.Empty(effects);
        Assert.Same(lost, afterLines);
        Assert.Same(lost, afterRefresh);
        Assert.Empty(refreshEffects);
    }

    [Fact]
    public void The_initial_state_shows_nothing_yet()
    {
        var state = DetailState.Initial("c");

        Assert.Equal("c", state.ContainerId);
        Assert.Null(state.Details);
        Assert.Same(LogBuffer.Empty, state.Logs);
        Assert.Same(StatsHistory.Empty, state.Stats);
        Assert.Equal(StreamStatus.Idle, state.LogStatus);
        Assert.Equal(StreamStatus.Idle, state.StatsStatus);
    }
}

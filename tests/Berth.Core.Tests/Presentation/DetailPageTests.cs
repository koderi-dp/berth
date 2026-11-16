using Berth.Presentation;
using Berth.Session;
using Berth.Tests.Session;

namespace Berth.Tests.Presentation;

public sealed class DetailPageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);

    private static ContainerRow Row(RowState state = RowState.Running, string status = "Up 5 minutes", ContainerAction? pending = null) =>
        new("c", "c", "web-1", "nginx", state, status, [], T0, pending);

    /// <summary>A session of container c that read it (<paramref name="running"/>) and whose streams are <paramref name="streams"/>.</summary>
    private static DetailState Session(bool running = true, StreamStatus streams = StreamStatus.Live, bool restarting = false)
    {
        var details = FakeEngineApi.Inspection("c", running).Details;
        details = details with { State = details.State with { Running = running || restarting, Restarting = restarting } };
        return DetailState.Initial("c") with
        {
            Details = details,
            LogStatus = streams,
            StatsStatus = running && !restarting ? streams : StreamStatus.Idle,
            InspectGeneration = 1,
            StreamGeneration = 1,
        };
    }

    private static readonly DetailState Reading = DetailState.Initial("c") with { InspectGeneration = 1 };

    private static DetailPageFacts Facts(
        bool active = true,
        bool connected = true,
        bool listLoaded = true,
        bool listReloaded = false,
        ContainerRow? previousRow = null,
        ContainerRow? row = null,
        DetailState? session = null) =>
        new(active, connected, listLoaded, listReloaded, previousRow, row, session);

    public static TheoryData<string, DetailPageFacts, DetailPageStep> Steps => new()
    {
        // A page kept in the navigation cache does nothing.
        { "hidden, no session", Facts(active: false, row: Row()), DetailPageStep.None },
        { "hidden, session lost", Facts(active: false, row: Row(), session: Session() with { ConnectionLost = true }), DetailPageStep.None },
        { "hidden, removed", Facts(active: false, session: Session() with { Removed = true }), DetailPageStep.None },
        { "hidden, list changed", Facts(active: false, listReloaded: true, previousRow: Row(), row: Row(RowState.Stopped, "Exited (0)"), session: Session()), DetailPageStep.None },

        // Open, close, reopen.
        { "shown, connected, no session", Facts(row: Row()), DetailPageStep.Open },
        { "shown, connected, list not loaded yet", Facts(listLoaded: false), DetailPageStep.Open },
        { "shown, not connected, no session", Facts(connected: false, row: Row()), DetailPageStep.None },
        { "connection lost", Facts(row: Row(), session: Session() with { ConnectionLost = true }), DetailPageStep.Close },
        { "connection lost, engine down", Facts(connected: false, row: Row(), session: Session() with { ConnectionLost = true }), DetailPageStep.Close },
        { "session open, engine down", Facts(connected: false, listReloaded: true, previousRow: Row(), row: Row(status: "Up 6 minutes"), session: Session()), DetailPageStep.None },

        // Removed.
        { "read answered 404", Facts(row: Row(), session: Session() with { Removed = true }), DetailPageStep.Leave },
        { "gone from a loaded list", Facts(listReloaded: true, previousRow: Row(), session: Session()), DetailPageStep.Leave },
        { "gone from a loaded list, no session", Facts(), DetailPageStep.Leave },
        { "not in the list kept while disconnected", Facts(connected: false), DetailPageStep.None },

        // Refresh only on a list load, only with no action running, only when the container may have changed run.
        { "nothing changed", Facts(listReloaded: true, previousRow: Row(), row: Row(), session: Session()), DetailPageStep.None },
        { "row changed, list not read again", Facts(previousRow: Row(), row: Row(RowState.Stopped, "Exited (0)"), session: Session()), DetailPageStep.None },
        { "stopped", Facts(listReloaded: true, previousRow: Row(), row: Row(RowState.Stopped, "Exited (0) 1 second ago"), session: Session()), DetailPageStep.Refresh },
        { "started", Facts(listReloaded: true, previousRow: Row(RowState.Stopped, "Exited (0) 1 minute ago"), row: Row(status: "Up 1 second"), session: Session(running: false, StreamStatus.Ended)), DetailPageStep.Refresh },
        { "restarted with the same state", Facts(listReloaded: true, previousRow: Row(status: "Up 5 minutes"), row: Row(status: "Up Less than a second"), session: Session()), DetailPageStep.Refresh },
        { "paused", Facts(listReloaded: true, previousRow: Row(), row: Row(RowState.Paused, "Up 5 minutes (Paused)"), session: Session()), DetailPageStep.Refresh },
        { "an action running", Facts(listReloaded: true, previousRow: Row(), row: Row(pending: ContainerAction.Restart), session: Session()), DetailPageStep.None },
        { "an action ended", Facts(listReloaded: true, previousRow: Row(pending: ContainerAction.Restart), row: Row(), session: Session()), DetailPageStep.Refresh },
        { "first list seen", Facts(listReloaded: true, row: Row(), session: Session()), DetailPageStep.None },
        { "first read not back yet", Facts(listReloaded: true, previousRow: Row(), row: Row(), session: Reading), DetailPageStep.None },

        // The row reads the same, but the session is out of step with it: a restart the session saw only half of.
        { "crash loop: same text, streams ended", Facts(listReloaded: true, previousRow: Row(status: "Up Less than a second"), row: Row(status: "Up Less than a second"), session: Session(streams: StreamStatus.Ended)), DetailPageStep.Refresh },
        { "docker restart: read while stopped", Facts(listReloaded: true, previousRow: Row(), row: Row(), session: Session(running: false, StreamStatus.Ended)), DetailPageStep.Refresh },
        { "restart policy: read while restarting", Facts(listReloaded: true, previousRow: Row(), row: Row(), session: Session(running: false, StreamStatus.Ended, restarting: true)), DetailPageStep.Refresh },
        { "stopped and read stopped", Facts(listReloaded: true, previousRow: Row(RowState.Stopped, "Exited (0)"), row: Row(RowState.Stopped, "Exited (0)"), session: Session(running: false, StreamStatus.Ended)), DetailPageStep.None },
        { "stopped, read running", Facts(listReloaded: true, previousRow: Row(RowState.Stopped, "Exited (0)"), row: Row(RowState.Stopped, "Exited (0)"), session: Session()), DetailPageStep.Refresh },
        { "out of step, list not read again", Facts(previousRow: Row(), row: Row(), session: Session(streams: StreamStatus.Ended)), DetailPageStep.None },
        { "a failed stream has its own Retry", Facts(listReloaded: true, previousRow: Row(), row: Row(), session: Session(streams: StreamStatus.Failed)), DetailPageStep.None },
    };

    [Theory]
    [MemberData(nameof(Steps))]
    public void The_page_decides_its_next_step(string @case, DetailPageFacts facts, DetailPageStep step)
    {
        Assert.True(step == DetailPage.Next(facts), @case);
    }

    [Fact]
    public void A_crash_loop_is_followed_from_one_list_load_to_the_next()
    {
        // Run 1 is live; it exits and is started again within the list's reload delay, so the row reads the same before and after.
        var row = Row(status: "Up Less than a second");
        var live = Session();
        Assert.Equal(DetailPageStep.None, DetailPage.Next(Facts(listReloaded: true, previousRow: row, row: row, session: live)));

        // The streams ended with the exit; the read that followed saw the gap and started nothing. The next list load refreshes.
        var ended = Session(running: false, StreamStatus.Ended, restarting: true);
        Assert.Equal(DetailPageStep.Refresh, DetailPage.Next(Facts(listReloaded: true, previousRow: row, row: row, session: ended)));

        // The refresh found run 2 and follows it: the next list load with nothing new does nothing.
        var following = Session(streams: StreamStatus.Loading);
        Assert.Equal(DetailPageStep.None, DetailPage.Next(Facts(listReloaded: true, previousRow: row, row: row, session: following)));
    }

    [Fact]
    public void A_live_session_is_shown_and_kept()
    {
        var live = Session();

        Assert.Same(live, DetailPage.Shown(live, lastLive: null, "c"));
        Assert.Same(live, DetailPage.KeepLive(live, lastLive: Session()));
        Assert.False(DetailPage.Waiting(live, live, connected: true));
    }

    [Fact]
    public void A_lost_session_is_never_shown_or_kept_the_last_live_one_is()
    {
        var live = Session();
        var lost = live with { ConnectionLost = true, LogStatus = StreamStatus.Ended, StatsStatus = StreamStatus.Ended };

        Assert.Same(live, DetailPage.KeepLive(lost, live));
        Assert.Same(live, DetailPage.Shown(lost, live, "c"));
        Assert.True(DetailPage.Waiting(live, lost, connected: false));
    }

    [Fact]
    public void While_the_session_is_closed_or_reopening_the_last_live_one_is_shown_waiting()
    {
        var live = Session();

        Assert.Same(live, DetailPage.KeepLive(null, live));
        Assert.Same(live, DetailPage.Shown(null, live, "c"));
        Assert.True(DetailPage.Waiting(live, null, connected: false));
        Assert.True(DetailPage.Waiting(live, null, connected: true));

        // A new session before its first read.
        Assert.Same(live, DetailPage.Shown(Reading, live, "c"));
        Assert.True(DetailPage.Waiting(live, Reading, connected: true));
    }

    [Fact]
    public void A_live_session_on_a_connection_that_dropped_is_shown_waiting()
    {
        var live = Session();

        Assert.True(DetailPage.Waiting(live, live, connected: false));
    }

    [Fact]
    public void The_last_live_session_of_another_container_is_never_shown()
    {
        var other = Session() with { ContainerId = "other" };

        Assert.Same(Reading, DetailPage.Shown(Reading, other, "c"));
        Assert.Null(DetailPage.Shown(null, other, "c"));
    }

    [Fact]
    public void Before_the_first_read_the_session_is_shown_as_it_is()
    {
        Assert.Same(Reading, DetailPage.Shown(Reading, lastLive: null, "c"));
        Assert.Null(DetailPage.KeepLive(Reading, lastLive: null));
        Assert.False(DetailPage.Waiting(Reading, Reading, connected: true));
        Assert.False(DetailPage.Waiting(null, null, connected: false));
    }

    [Fact]
    public void The_waiting_text_is_neutral()
    {
        Assert.Equal("Waiting for the engine…", DetailPage.WaitingText);
    }
}

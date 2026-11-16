using Berth.Engine.Models;
using Berth.Session;
using Berth.State;

namespace Berth.Tests.Session;

/// <summary>Reading one image for the run dialog and the Images page's Inspect.</summary>
public sealed partial class SessionReducerTests
{
    private static ImageInspection InspectionOf(string id, params string[] ports) =>
        new(FakeEngineApi.ImageDetails(id, ports), $$"""{"Id":"{{id}}"}""");

    [Fact]
    public void An_image_inspect_asked_for_reads_the_image_with_a_new_generation()
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected, new ImageInspectRequested("sha256:a"));

        Assert.Equal(new ImageInspect("sha256:a", connected.Ticket + 1, null, null), state.ImageInspect);
        Assert.True(state.ImageInspect!.IsLoading);
        Assert.Equal(connected.Ticket + 1, state.Ticket);
        Assert.Equal([new InspectImageEffect("sha256:a", connected.Ticket + 1)], effects);
    }

    [Fact]
    public void The_answer_is_kept_until_dismissed()
    {
        var (asked, _) = Reduce(Connected(), new ImageInspectRequested("sha256:a"));
        var inspection = InspectionOf("sha256:a", "80/tcp");

        var (read, effects) = Reduce(asked, new ImageInspected(asked.ImageInspect!.Generation, inspection));

        Assert.Same(inspection, read.ImageInspect!.Inspection);
        Assert.False(read.ImageInspect.IsLoading);
        Assert.Empty(effects);
        Assert.Null(Reduce(read, new ImageInspectDismissed()).State.ImageInspect);
    }

    [Fact]
    public void A_second_image_asked_for_replaces_the_first_whose_answer_is_dropped()
    {
        var (first, _) = Reduce(Connected(), new ImageInspectRequested("sha256:a"));
        var firstGeneration = first.ImageInspect!.Generation;
        var (second, effects) = Reduce(first, new ImageInspectRequested("sha256:b"));
        Assert.Equal([new InspectImageEffect("sha256:b", second.ImageInspect!.Generation)], effects);

        var (late, _) = Reduce(second, new ImageInspected(firstGeneration, InspectionOf("sha256:a")));
        var (lateFailure, _) = Reduce(late, new ImageInspectFailed(firstGeneration, "No such image", EngineUnreachable: false));

        Assert.Equal(new ImageInspect("sha256:b", second.ImageInspect.Generation, null, null), lateFailure.ImageInspect);
        var answer = InspectionOf("sha256:b");
        Assert.Same(answer, Reduce(lateFailure, new ImageInspected(second.ImageInspect.Generation, answer)).State.ImageInspect!.Inspection);
    }

    [Fact]
    public void A_refused_inspect_keeps_the_engines_message_as_is_and_stays_connected()
    {
        var (asked, _) = Reduce(Connected(), new ImageInspectRequested("berth-check-nope"));

        var (state, effects) = Reduce(asked, new ImageInspectFailed(asked.ImageInspect!.Generation, "No such image: berth-check-nope:latest", EngineUnreachable: false));

        Assert.Equal("No such image: berth-check-nope:latest", state.ImageInspect!.Error);
        Assert.False(state.ImageInspect.IsLoading);
        Assert.Null(state.ActionError);
        Assert.Equal(ConnectionStatus.Connected, state.Status);
        Assert.Empty(effects);
    }

    [Fact]
    public void An_inspect_that_cannot_reach_the_engine_moves_to_disconnected_and_is_forgotten()
    {
        var (asked, _) = Reduce(Connected(), new ImageInspectRequested("sha256:a"));

        var (state, _) = Reduce(asked, new ImageInspectFailed(asked.ImageInspect!.Generation, "connection refused", EngineUnreachable: true));

        Assert.Equal(ConnectionStatus.Disconnected, state.Status);
        Assert.Null(state.ImageInspect);
    }

    [Fact]
    public void Any_change_of_connection_forgets_the_image_read_and_its_late_answer()
    {
        var (asked, _) = Reduce(Connected(), new ImageInspectRequested("sha256:a"));
        var generation = asked.ImageInspect!.Generation;

        var (lost, _) = Reduce(asked, new ConnectionLost("connection reset"));
        var (restarted, _) = Reduce(asked, new ConnectRequested());

        Assert.Null(lost.ImageInspect);
        Assert.Null(restarted.ImageInspect);
        Assert.Null(Reduce(restarted, new ImageInspected(generation, InspectionOf("sha256:a"))).State.ImageInspect);
    }

    [Fact]
    public void An_image_inspect_asked_for_while_not_connected_does_nothing()
    {
        var (state, effects) = Reduce(EngineState.Initial, new ImageInspectRequested("sha256:a"));

        Assert.Null(state.ImageInspect);
        Assert.Empty(effects);
    }
}

using Berth.Presentation;
using Berth.State;

namespace Berth.Tests.Presentation;

public sealed class RunOutcomeTests
{
    [Fact]
    public void Each_run_status_has_its_line_and_the_engine_message_is_shown_as_is()
    {
        Assert.Equal("Creating and starting the container…", RunOutcome.Text(new ContainerRun(1, "alpine", RunStatus.InProgress, null, null)));
        Assert.Null(RunOutcome.Text(new ContainerRun(1, "alpine", RunStatus.Started, "c1", null)));
        Assert.Equal("No such image: alpine", RunOutcome.Text(new ContainerRun(1, "alpine", RunStatus.Failed, null, "No such image: alpine")));
        Assert.Equal(
            "The container was created but could not be started: port is already allocated",
            RunOutcome.Text(new ContainerRun(1, "alpine", RunStatus.StartFailed, "c1", "port is already allocated")));
    }

    [Fact]
    public void An_interrupted_run_says_the_connection_changed_and_whether_the_container_may_exist()
    {
        Assert.Equal(
            "The connection to the engine changed. The container may have been created; look for it in the Containers list.",
            RunOutcome.Text(new ContainerRun(1, "alpine", RunStatus.Interrupted, null, null)));
        Assert.Equal(
            "The connection to the engine changed after the container was created and before it started.",
            RunOutcome.Text(new ContainerRun(1, "alpine", RunStatus.Interrupted, "c1", null)));
    }

    [Fact]
    public void A_run_not_sent_says_berth_is_not_connected()
    {
        Assert.Equal(
            "Not sent: Berth is not connected to the engine. Try again once it is.",
            RunOutcome.Text(new ContainerRun(1, "alpine", RunStatus.NotConnected, null, null)));
    }
}

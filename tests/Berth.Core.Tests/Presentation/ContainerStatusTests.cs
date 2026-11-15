using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class ContainerStatusTests
{
    [Theory]
    [InlineData("Exited (137) 5 hours ago", true)]
    [InlineData("Exited (1) Less than a second ago", true)]
    [InlineData("Exited (0) 3 minutes ago", false)]
    [InlineData("Up 3 hours", false)]
    [InlineData("Up 5 hours (healthy)", false)]
    [InlineData("Created", false)]
    [InlineData("Dead", true)]
    [InlineData("Exited", false)]
    [InlineData("Exited (abc) now", false)]
    [InlineData("", false)]
    public void A_non_zero_exit_code_is_a_failed_exit(string status, bool failed) =>
        Assert.Equal(failed, ContainerStatus.IsFailedExit(status));

    [Theory]
    [InlineData(RowState.Running, StateShape.Filled)]
    [InlineData(RowState.Paused, StateShape.Half)]
    [InlineData(RowState.Restarting, StateShape.Half)]
    [InlineData(RowState.Stopped, StateShape.Ring)]
    public void Each_state_has_its_own_shape(RowState state, StateShape shape) =>
        Assert.Equal(shape, ContainerStatus.Shape(state));

    [Fact]
    public void A_project_whose_containers_all_run_is_filled() =>
        Assert.Equal(StateShape.Filled, ContainerStatus.GroupShape([RowState.Running, RowState.Running]));

    [Fact]
    public void A_project_with_nothing_running_is_a_ring() =>
        Assert.Equal(StateShape.Ring, ContainerStatus.GroupShape([RowState.Stopped, RowState.Stopped]));

    [Theory]
    [InlineData(RowState.Running, RowState.Stopped)]
    [InlineData(RowState.Running, RowState.Paused)]
    [InlineData(RowState.Paused, RowState.Stopped)]
    [InlineData(RowState.Restarting, RowState.Restarting)]
    public void A_project_in_between_is_half(RowState first, RowState second) =>
        Assert.Equal(StateShape.Half, ContainerStatus.GroupShape([first, second]));

    [Fact]
    public void A_project_with_no_rows_is_a_ring() =>
        Assert.Equal(StateShape.Ring, ContainerStatus.GroupShape([]));
}

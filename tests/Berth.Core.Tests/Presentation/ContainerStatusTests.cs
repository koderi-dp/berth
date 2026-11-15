using Berth.Engine.Models;
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

    // The detail page's status line: the state, and since when.

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static ContainerStateDetails Inspected(bool running, DateTimeOffset? startedAt) =>
        new(running ? "running" : "exited", running, Paused: false, Restarting: false, ExitCode: 0, Error: "", startedAt, FinishedAt: null, Health: null);

    [Fact]
    public void A_running_container_says_since_when_it_runs() =>
        Assert.Equal("Running (52 minutes ago)", ContainerStatus.Headline(RowState.Running, "Up 52 minutes", Inspected(true, Now.AddMinutes(-52)), Now));

    [Fact]
    public void A_running_container_not_read_yet_says_running() =>
        Assert.Equal("Running", ContainerStatus.Headline(RowState.Running, "Up 52 minutes", inspected: null, Now));

    [Fact]
    public void A_running_container_whose_last_read_was_stopped_says_running_without_a_time() =>
        Assert.Equal("Running", ContainerStatus.Headline(RowState.Running, "Up 1 second", Inspected(false, Now.AddHours(-3)), Now));

    [Theory]
    [InlineData(RowState.Paused, "Up 2 hours (Paused)", "Paused")]
    [InlineData(RowState.Restarting, "Restarting (1) 2 seconds ago", "Restarting")]
    [InlineData(RowState.Stopped, "Exited (137) 3 hours ago", "Exited (137) 3 hours ago")]
    [InlineData(RowState.Stopped, "Created", "Created")]
    [InlineData(RowState.Stopped, "", "Stopped")]
    public void Other_states_say_what_the_list_says_or_their_name(RowState state, string listStatus, string headline) =>
        Assert.Equal(headline, ContainerStatus.Headline(state, listStatus, Inspected(false, Now.AddHours(-3)), Now));
}

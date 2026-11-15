using Berth.Presentation;
using Berth.Session;

namespace Berth.Tests.Presentation;

public sealed class BulkSelectionTests
{
    private static ContainerRow Row(string id, RowState state = RowState.Running, ContainerAction? pending = null, string? name = null) =>
        new(id, id, name ?? id, "nginx:alpine", state, "", [], DateTimeOffset.UnixEpoch, pending);

    private static readonly ContainerRow Running = Row("run", RowState.Running);
    private static readonly ContainerRow Paused = Row("paused", RowState.Paused);
    private static readonly ContainerRow Stopped = Row("stopped", RowState.Stopped);
    private static readonly ContainerRow PendingStopped = Row("pend-stopped", RowState.Stopped, ContainerAction.Start);
    private static readonly ContainerRow PendingRunning = Row("pend-run", RowState.Running, ContainerAction.Stop);

    private static readonly ContainerRow[] Visible = [Running, Paused, Stopped, PendingStopped, PendingRunning];

    private static HashSet<string> Checked(params string[] ids) => [.. ids];

    [Fact]
    public void Selected_count_counts_checked_visible_rows_only()
    {
        var selection = Checked("run", "stopped", "hidden");

        Assert.Equal(2, BulkSelection.SelectedCount(Visible, selection));
    }

    [Fact]
    public void Selected_count_includes_pending_rows()
    {
        Assert.Equal(2, BulkSelection.SelectedCount(Visible, Checked("pend-run", "pend-stopped")));
    }

    [Fact]
    public void Start_sends_checked_visible_stopped_rows_that_are_not_pending()
    {
        var selection = Checked("run", "paused", "stopped", "pend-stopped", "pend-run", "hidden");

        Assert.Equal<string>(["stopped"], BulkSelection.StartIds(Visible, selection));
    }

    [Fact]
    public void Stop_sends_checked_visible_rows_that_are_not_stopped_and_not_pending()
    {
        var selection = Checked("run", "paused", "stopped", "pend-stopped", "pend-run", "hidden");

        Assert.Equal<string>(["run", "paused"], BulkSelection.StopIds(Visible, selection));
    }

    [Fact]
    public void Delete_sends_checked_visible_rows_that_are_not_pending()
    {
        var selection = Checked("run", "paused", "stopped", "pend-stopped", "pend-run", "hidden");

        Assert.Equal<string>(["run", "paused", "stopped"], BulkSelection.DeleteIds(Visible, selection));
    }

    [Fact]
    public void Nothing_checked_sends_nothing()
    {
        Assert.Empty(BulkSelection.StartIds(Visible, Checked()));
        Assert.Empty(BulkSelection.StopIds(Visible, Checked()));
        Assert.Empty(BulkSelection.DeleteIds(Visible, Checked()));
        Assert.Equal(0, BulkSelection.SelectedCount(Visible, Checked()));
    }

    [Fact]
    public void Removal_at_confirm_skips_rows_that_are_gone_or_pending()
    {
        var removal = BulkSelection.Removal(Visible, ["stopped", "pend-run", "gone"]);

        Assert.Equal<string>(["stopped"], removal.Ids);
        Assert.False(removal.Force);
    }

    [Fact]
    public void Removal_at_confirm_forces_when_any_row_is_not_stopped()
    {
        var removal = BulkSelection.Removal(Visible, ["stopped", "paused"]);

        Assert.Equal<string>(["stopped", "paused"], removal.Ids);
        Assert.True(removal.Force);
    }

    [Fact]
    public void Removal_keeps_the_requested_order()
    {
        var removal = BulkSelection.Removal(Visible, ["stopped", "run"]);

        Assert.Equal<string>(["stopped", "run"], removal.Ids);
    }

    [Fact]
    public void Delete_text_for_one_stopped_container()
    {
        Assert.Equal(
            "web will be removed. Its data that is not in a volume is lost.",
            BulkSelection.DeleteText([Row("1", RowState.Stopped, name: "web")]));
    }

    [Fact]
    public void Delete_text_for_one_running_container()
    {
        Assert.Equal(
            "web will be removed. Its data that is not in a volume is lost. It is running and will be stopped first.",
            BulkSelection.DeleteText([Row("1", RowState.Running, name: "web")]));
    }

    [Fact]
    public void Delete_text_for_several_names_them_one_per_line()
    {
        Assert.Equal(
            "2 containers will be removed:\nweb\ndb\nTheir data that is not in a volume is lost.",
            BulkSelection.DeleteText([Row("1", RowState.Stopped, name: "web"), Row("2", RowState.Stopped, name: "db")]));
    }

    [Fact]
    public void Delete_text_for_several_with_one_running_adds_the_stop_sentence()
    {
        Assert.Equal(
            "2 containers will be removed:\nweb\ndb\nTheir data that is not in a volume is lost. Those that are running will be stopped first.",
            BulkSelection.DeleteText([Row("1", RowState.Paused, name: "web"), Row("2", RowState.Stopped, name: "db")]));
    }

    [Fact]
    public void Delete_text_names_ten_containers_at_most_then_how_many_more()
    {
        ContainerRow[] rows = [.. Enumerable.Range(1, 13).Select(i => Row(i.ToString(), RowState.Stopped, name: "c" + i))];

        var expected = "13 containers will be removed:\n"
            + string.Join("\n", Enumerable.Range(1, 10).Select(i => "c" + i))
            + "\nand 3 more\nTheir data that is not in a volume is lost.";
        Assert.Equal(expected, BulkSelection.DeleteText(rows));
    }

    [Fact]
    public void Delete_text_for_exactly_ten_has_no_more_line()
    {
        ContainerRow[] rows = [.. Enumerable.Range(1, 10).Select(i => Row(i.ToString(), RowState.Stopped, name: "c" + i))];

        Assert.DoesNotContain("more", BulkSelection.DeleteText(rows));
    }

    [Fact]
    public void Empty_text_without_any_container()
    {
        Assert.Equal("No containers on this engine.", BulkSelection.EmptyText(engineHasContainers: false, new ContainerFilter("web", RunningOnly: true)));
    }

    [Fact]
    public void Empty_text_with_a_search()
    {
        Assert.Equal("No containers match the search.", BulkSelection.EmptyText(engineHasContainers: true, new ContainerFilter("web", RunningOnly: true)));
        Assert.Equal("No containers match the search.", BulkSelection.EmptyText(engineHasContainers: true, new ContainerFilter("web", RunningOnly: false)));
    }

    [Fact]
    public void Empty_text_with_running_only_and_a_blank_search()
    {
        Assert.Equal("No containers are running.", BulkSelection.EmptyText(engineHasContainers: true, new ContainerFilter("  ", RunningOnly: true)));
    }
}

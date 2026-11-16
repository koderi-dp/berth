using Berth.Presentation;
using Berth.State;

namespace Berth.Tests.Presentation;

/// <summary>The actions of a row of the images table: its Run button, "more" menu, right-click menu and Delete.</summary>
public sealed class ImageRowActionsTests
{
    private static ImageRow Row(string key = "nginx:latest", bool tagged = true, ItemAction? pending = null) =>
        new(key, "sha256:a", "a", tagged ? "nginx" : ImageRows.None, tagged ? "latest" : ImageRows.None, tagged, tagged ? 1 : 0,
            DateTimeOffset.UnixEpoch, 1000, 0, [], pending);

    private static RowAction Find(RowActions actions, RowActionKind kind) =>
        actions.Sections.SelectMany(s => s).Single(a => a.Kind == kind);

    [Fact]
    public void A_tagged_image_has_run_then_pull_and_inspect_then_the_copies_then_delete()
    {
        var actions = RowActions.ForImage(Row(), connected: true);

        Assert.Equal(
            [
                [RowActionKind.Run],
                [RowActionKind.PullAgain, RowActionKind.Inspect],
                [RowActionKind.CopyName, RowActionKind.CopyId],
                [RowActionKind.Delete],
            ],
            actions.Sections.Select(s => s.Select(a => a.Kind).ToArray()).ToArray());
        Assert.All(actions.Sections.SelectMany(s => s), a => Assert.True(a.IsEnabled));
        Assert.Equal(["Run", "Pull again", "Inspect", "Copy name", "Copy ID", "Delete"], actions.Sections.SelectMany(s => s).Select(a => a.Label));
        Assert.All(actions.Sections.SelectMany(s => s), a => Assert.Equal(["nginx:latest"], a.Ids.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_and_delete_are_the_buttons_at_every_width_and_the_rest_is_the_more_menu(bool compact)
    {
        var actions = RowActions.ForImage(Row(), connected: true);

        Assert.Equal([RowActionKind.Run], actions.Buttons(compact).Select(a => a.Kind));
        Assert.True(Find(actions, RowActionKind.Run).IsMain);
        Assert.Equal(
            [[RowActionKind.PullAgain, RowActionKind.Inspect], [RowActionKind.CopyName, RowActionKind.CopyId]],
            actions.MoreMenu(compact).Select(s => s.Select(a => a.Kind).ToArray()).ToArray());
        Assert.Equal(RowActionKind.Delete, actions.Delete.Kind);
    }

    [Fact]
    public void An_untagged_image_runs_by_id_but_has_no_name_to_pull_or_copy()
    {
        var actions = RowActions.ForImage(Row("sha256:a", tagged: false), connected: true);

        Assert.True(Find(actions, RowActionKind.Run).IsEnabled);
        Assert.False(Find(actions, RowActionKind.PullAgain).IsEnabled);
        Assert.False(Find(actions, RowActionKind.CopyName).IsEnabled);
        Assert.True(Find(actions, RowActionKind.CopyId).IsEnabled);
        Assert.True(actions.Delete.IsEnabled);
    }

    [Fact]
    public void While_not_connected_only_the_copies_act()
    {
        var actions = RowActions.ForImage(Row(), connected: false);

        Assert.Equal(
            [RowActionKind.CopyName, RowActionKind.CopyId],
            actions.Sections.SelectMany(s => s).Where(a => a.IsEnabled).Select(a => a.Kind));
    }

    [Fact]
    public void A_pending_image_cannot_be_run_or_deleted_again_but_can_still_be_read_and_copied()
    {
        var actions = RowActions.ForImage(Row(pending: ItemAction.Remove), connected: true);

        Assert.False(Find(actions, RowActionKind.Run).IsEnabled);
        Assert.False(Find(actions, RowActionKind.PullAgain).IsEnabled);
        Assert.False(actions.Delete.IsEnabled);
        Assert.True(Find(actions, RowActionKind.Inspect).IsEnabled);
        Assert.True(Find(actions, RowActionKind.CopyId).IsEnabled);
    }
}

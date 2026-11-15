using Berth.Presentation;
using Berth.Session;

namespace Berth.Tests.Presentation;

public sealed class RowActionsTests
{
    private static readonly Uri Site = new("http://localhost:8080/");

    private static ContainerRow Row(string id, RowState state = RowState.Running, ContainerAction? pending = null, bool published = false) =>
        new(id, id, id, "nginx:alpine", state, "", published ? [new PortLink("8080:80", Site)] : [new PortLink("80", null)], DateTimeOffset.UnixEpoch, pending);

    private static RowActionKind[][] Kinds(IReadOnlyList<IReadOnlyList<RowAction>> sections) =>
        [.. sections.Select(s => s.Select(a => a.Kind).ToArray())];

    private static RowAction Find(RowActions actions, RowActionKind kind) =>
        actions.Sections.SelectMany(s => s).Single(a => a.Kind == kind);

    // The context menu: every action, in order, in sections.

    [Fact]
    public void A_running_container_with_a_published_port_has_every_action_in_order()
    {
        var actions = RowActions.ForContainer(Row("web", published: true), connected: true);

        Assert.Equal(
            [
                [RowActionKind.Stop, RowActionKind.Restart, RowActionKind.Pause],
                [RowActionKind.OpenInBrowser, RowActionKind.CopyName, RowActionKind.CopyId],
                [RowActionKind.Delete],
            ],
            Kinds(actions.Sections));
        Assert.Equal(Site, actions.BrowserUrl);
    }

    [Fact]
    public void A_stopped_container_starts_and_has_no_browser_item_without_a_published_port()
    {
        var actions = RowActions.ForContainer(Row("db", RowState.Stopped), connected: true);

        Assert.Equal(
            [
                [RowActionKind.Start, RowActionKind.Restart, RowActionKind.Pause],
                [RowActionKind.CopyName, RowActionKind.CopyId],
                [RowActionKind.Delete],
            ],
            Kinds(actions.Sections));
        Assert.Null(actions.BrowserUrl);
    }

    [Fact]
    public void A_paused_container_resumes()
    {
        var actions = RowActions.ForContainer(Row("p", RowState.Paused), connected: true);

        Assert.Equal([RowActionKind.Stop, RowActionKind.Restart, RowActionKind.Resume], Kinds(actions.Sections)[0]);
    }

    [Fact]
    public void Labels_name_the_actions()
    {
        var actions = RowActions.ForContainer(Row("web", published: true), connected: true);

        Assert.Equal(
            ["Stop", "Restart", "Pause", "Open in browser", "Copy name", "Copy ID", "Delete"],
            actions.Sections.SelectMany(s => s).Select(a => a.Label));
        Assert.Equal("Resume", Find(RowActions.ForContainer(Row("p", RowState.Paused), connected: true), RowActionKind.Resume).Label);
        Assert.Equal("Start", Find(RowActions.ForContainer(Row("s", RowState.Stopped), connected: true), RowActionKind.Start).Label);
    }

    // Enabled: exactly as the buttons were.

    [Fact]
    public void Pause_is_enabled_only_for_a_running_container()
    {
        Assert.True(Find(RowActions.ForContainer(Row("r"), connected: true), RowActionKind.Pause).IsEnabled);
        Assert.False(Find(RowActions.ForContainer(Row("s", RowState.Stopped), connected: true), RowActionKind.Pause).IsEnabled);
        Assert.False(Find(RowActions.ForContainer(Row("x", RowState.Restarting), connected: true), RowActionKind.Pause).IsEnabled);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, ContainerAction.Stop)]
    public void Engine_actions_are_disabled_while_not_connected_or_busy_and_the_rest_stay_enabled(bool connected, ContainerAction? pending)
    {
        var actions = RowActions.ForContainer(Row("web", RowState.Paused, pending, published: true), connected);

        var enabled = actions.Sections.SelectMany(s => s).Where(a => a.IsEnabled).Select(a => a.Kind);
        Assert.Equal([RowActionKind.OpenInBrowser, RowActionKind.CopyName, RowActionKind.CopyId], enabled);
    }

    [Fact]
    public void Engine_actions_of_a_container_send_its_id()
    {
        var actions = RowActions.ForContainer(Row("web"), connected: true);

        Assert.All(actions.Sections.SelectMany(s => s).Where(a => a.EngineAction is not null), a => Assert.Equal<string>(["web"], a.Ids));
        Assert.Equal(ContainerAction.Stop, Find(actions, RowActionKind.Stop).EngineAction);
        Assert.Equal(ContainerAction.Restart, Find(actions, RowActionKind.Restart).EngineAction);
        Assert.Equal(ContainerAction.Pause, Find(actions, RowActionKind.Pause).EngineAction);
        Assert.Equal(ContainerAction.Unpause, Find(RowActions.ForContainer(Row("p", RowState.Paused), connected: true), RowActionKind.Resume).EngineAction);
        Assert.Null(Find(actions, RowActionKind.Delete).EngineAction); // confirmed first, then removed
        Assert.Null(Find(actions, RowActionKind.CopyId).EngineAction);
    }

    // Buttons and the "more" menu: the same actions split by window width.

    [Fact]
    public void A_wide_container_row_has_buttons_for_its_engine_actions_and_the_rest_in_the_menu()
    {
        var actions = RowActions.ForContainer(Row("web", published: true), connected: true);

        Assert.Equal([RowActionKind.Stop, RowActionKind.Restart, RowActionKind.Pause], actions.Buttons(compact: false).Select(a => a.Kind));
        Assert.Equal([[RowActionKind.OpenInBrowser, RowActionKind.CopyName, RowActionKind.CopyId]], Kinds(actions.MoreMenu(compact: false)));
        Assert.Equal(RowActionKind.Delete, actions.Delete.Kind);
    }

    [Fact]
    public void A_compact_container_row_keeps_the_main_button_and_moves_restart_and_pause_into_the_menu()
    {
        var actions = RowActions.ForContainer(Row("web"), connected: true);

        Assert.Equal([RowActionKind.Stop], actions.Buttons(compact: true).Select(a => a.Kind));
        Assert.Equal(
            [[RowActionKind.Restart, RowActionKind.Pause], [RowActionKind.CopyName, RowActionKind.CopyId]],
            Kinds(actions.MoreMenu(compact: true)));
    }

    [Fact]
    public void The_main_action_is_marked()
    {
        Assert.True(Find(RowActions.ForContainer(Row("r"), connected: true), RowActionKind.Stop).IsMain);
        Assert.True(Find(RowActions.ForContainer(Row("s", RowState.Stopped), connected: true), RowActionKind.Start).IsMain);
        Assert.False(Find(RowActions.ForContainer(Row("r"), connected: true), RowActionKind.Restart).IsMain);
    }

    // Compose projects.

    [Fact]
    public void A_project_with_stopped_and_active_rows_starts_the_stopped_and_stops_the_active()
    {
        var actions = RowActions.ForProject([Row("a"), Row("b", RowState.Stopped), Row("c", RowState.Paused)], connected: true);

        Assert.Equal(
            [[RowActionKind.Start, RowActionKind.Stop, RowActionKind.Restart], [RowActionKind.Delete]],
            Kinds(actions.Sections));
        Assert.Equal<string>(["b"], Find(actions, RowActionKind.Start).Ids);
        Assert.Equal<string>(["a", "c"], Find(actions, RowActionKind.Stop).Ids);
        Assert.Equal<string>(["a", "b", "c"], Find(actions, RowActionKind.Restart).Ids);
        Assert.Equal<string>(["a", "b", "c"], Find(actions, RowActionKind.Delete).Ids);
        Assert.True(Find(actions, RowActionKind.Stop).IsMain);
        Assert.False(Find(actions, RowActionKind.Start).IsMain);
    }

    [Fact]
    public void A_project_with_nothing_active_has_start_and_no_stop()
    {
        var actions = RowActions.ForProject([Row("a", RowState.Stopped)], connected: true);

        Assert.Equal([RowActionKind.Start, RowActionKind.Restart], Kinds(actions.Sections)[0]);
        Assert.True(Find(actions, RowActionKind.Start).IsMain);
    }

    [Fact]
    public void A_project_with_nothing_stopped_has_stop_and_no_start()
    {
        var actions = RowActions.ForProject([Row("a"), Row("b", RowState.Paused)], connected: true);

        Assert.Equal([RowActionKind.Stop, RowActionKind.Restart], Kinds(actions.Sections)[0]);
    }

    [Fact]
    public void A_wide_project_row_has_a_button_for_each_action_and_no_menu()
    {
        var actions = RowActions.ForProject([Row("a"), Row("b", RowState.Stopped)], connected: true);

        Assert.Equal([RowActionKind.Start, RowActionKind.Stop, RowActionKind.Restart], actions.Buttons(compact: false).Select(a => a.Kind));
        Assert.Empty(actions.MoreMenu(compact: false));
    }

    [Fact]
    public void A_compact_project_row_keeps_the_main_button_and_moves_the_rest_into_the_menu()
    {
        var actions = RowActions.ForProject([Row("a"), Row("b", RowState.Stopped)], connected: true);

        Assert.Equal([RowActionKind.Stop], actions.Buttons(compact: true).Select(a => a.Kind));
        Assert.Equal([[RowActionKind.Start, RowActionKind.Restart]], Kinds(actions.MoreMenu(compact: true)));
    }

    [Fact]
    public void A_project_acts_only_while_connected_and_nothing_in_it_is_busy()
    {
        Assert.All(
            RowActions.ForProject([Row("a"), Row("b", RowState.Stopped, ContainerAction.Start)], connected: true).Sections.SelectMany(s => s),
            a => Assert.False(a.IsEnabled));
        Assert.All(
            RowActions.ForProject([Row("a")], connected: false).Sections.SelectMany(s => s),
            a => Assert.False(a.IsEnabled));
        Assert.All(
            RowActions.ForProject([Row("a")], connected: true).Sections.SelectMany(s => s),
            a => Assert.True(a.IsEnabled));
    }
}

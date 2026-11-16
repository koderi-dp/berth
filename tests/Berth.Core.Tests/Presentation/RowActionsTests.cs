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
                [RowActionKind.ViewDetails],
                [RowActionKind.Stop, RowActionKind.Restart, RowActionKind.Pause],
                [RowActionKind.OpenInBrowser, RowActionKind.CopyName, RowActionKind.CopyId, RowActionKind.CopyRunPowerShell, RowActionKind.CopyRunBash],
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
                [RowActionKind.ViewDetails],
                [RowActionKind.Start, RowActionKind.Restart, RowActionKind.Pause],
                [RowActionKind.CopyName, RowActionKind.CopyId, RowActionKind.CopyRunPowerShell, RowActionKind.CopyRunBash],
                [RowActionKind.Delete],
            ],
            Kinds(actions.Sections));
        Assert.Null(actions.BrowserUrl);
    }

    [Fact]
    public void A_paused_container_resumes()
    {
        var actions = RowActions.ForContainer(Row("p", RowState.Paused), connected: true);

        Assert.Equal([RowActionKind.Stop, RowActionKind.Restart, RowActionKind.Resume], Kinds(actions.Sections)[1]);
    }

    [Fact]
    public void Labels_name_the_actions()
    {
        var actions = RowActions.ForContainer(Row("web", published: true), connected: true);

        Assert.Equal(
            ["View details", "Stop", "Restart", "Pause", "Open in browser", "Copy name", "Copy ID", "Copy docker run (PowerShell)", "Copy docker run (bash)", "Delete"],
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

    [Fact]
    public void While_not_connected_only_the_actions_that_stay_on_this_machine_are_enabled()
    {
        var actions = RowActions.ForContainer(Row("web", RowState.Paused, published: true), connected: false);

        var enabled = actions.Sections.SelectMany(s => s).Where(a => a.IsEnabled).Select(a => a.Kind);
        Assert.Equal([RowActionKind.ViewDetails, RowActionKind.OpenInBrowser, RowActionKind.CopyName, RowActionKind.CopyId], enabled);
    }

    [Fact]
    public void While_busy_the_engine_actions_are_disabled_and_copying_the_run_command_is_not()
    {
        var actions = RowActions.ForContainer(Row("web", RowState.Paused, ContainerAction.Stop, published: true), connected: true);

        var enabled = actions.Sections.SelectMany(s => s).Where(a => a.IsEnabled).Select(a => a.Kind);
        Assert.Equal(
            [
                RowActionKind.ViewDetails, RowActionKind.OpenInBrowser, RowActionKind.CopyName, RowActionKind.CopyId,
                RowActionKind.CopyRunPowerShell, RowActionKind.CopyRunBash,
            ],
            enabled);
    }

    // Copy docker run: one item per shell, because quoting differs; it reads the container from the engine, so it needs the connection.

    [Fact]
    public void Copy_docker_run_comes_for_each_shell_with_its_shell()
    {
        var actions = RowActions.ForContainer(Row("web"), connected: true);

        Assert.Equal(ShellKind.PowerShell, Find(actions, RowActionKind.CopyRunPowerShell).RunCommandShell);
        Assert.Equal(ShellKind.Posix, Find(actions, RowActionKind.CopyRunBash).RunCommandShell);
        Assert.All(
            actions.Sections.SelectMany(s => s).Where(a => a.Kind is not (RowActionKind.CopyRunPowerShell or RowActionKind.CopyRunBash)),
            a => Assert.Null(a.RunCommandShell));
        Assert.Null(Find(actions, RowActionKind.CopyRunPowerShell).EngineAction);
        Assert.Equal<string>(["web"], Find(actions, RowActionKind.CopyRunBash).Ids);
    }

    [Fact]
    public void Copy_docker_run_for_PowerShell_says_which_PowerShell_it_is_for()
    {
        var actions = RowActions.ForContainer(Row("web"), connected: true);

        Assert.Equal(
            "For PowerShell 7. Windows PowerShell 5.1 drops double quotes inside values.",
            Find(actions, RowActionKind.CopyRunPowerShell).Description);
        Assert.Null(Find(actions, RowActionKind.CopyRunBash).Description);
        Assert.Null(Find(actions, RowActionKind.CopyName).Description);
    }

    [Fact]
    public void Copy_docker_run_is_disabled_while_not_connected()
    {
        var actions = RowActions.ForContainer(Row("web"), connected: false);

        Assert.False(Find(actions, RowActionKind.CopyRunPowerShell).IsEnabled);
        Assert.False(Find(actions, RowActionKind.CopyRunBash).IsEnabled);
    }

    [Fact]
    public void Copy_docker_run_is_never_a_button()
    {
        var actions = RowActions.ForContainer(Row("web"), connected: true);

        Assert.DoesNotContain(actions.Buttons(compact: false), a => a.RunCommandShell is not null);
        Assert.DoesNotContain(actions.Buttons(compact: true), a => a.RunCommandShell is not null);
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
        Assert.Equal(
            [
                [RowActionKind.ViewDetails],
                [RowActionKind.OpenInBrowser, RowActionKind.CopyName, RowActionKind.CopyId, RowActionKind.CopyRunPowerShell, RowActionKind.CopyRunBash],
            ],
            Kinds(actions.MoreMenu(compact: false)));
        Assert.Equal(RowActionKind.Delete, actions.Delete.Kind);
    }

    [Fact]
    public void A_compact_container_row_keeps_the_main_button_and_moves_restart_and_pause_into_the_menu()
    {
        var actions = RowActions.ForContainer(Row("web"), connected: true);

        Assert.Equal([RowActionKind.Stop], actions.Buttons(compact: true).Select(a => a.Kind));
        Assert.Equal(
            [
                [RowActionKind.ViewDetails],
                [RowActionKind.Restart, RowActionKind.Pause],
                [RowActionKind.CopyName, RowActionKind.CopyId, RowActionKind.CopyRunPowerShell, RowActionKind.CopyRunBash],
            ],
            Kinds(actions.MoreMenu(compact: true)));
    }

    [Fact]
    public void The_main_action_is_marked()
    {
        Assert.True(Find(RowActions.ForContainer(Row("r"), connected: true), RowActionKind.Stop).IsMain);
        Assert.True(Find(RowActions.ForContainer(Row("s", RowState.Stopped), connected: true), RowActionKind.Start).IsMain);
        Assert.False(Find(RowActions.ForContainer(Row("r"), connected: true), RowActionKind.Restart).IsMain);
    }

    // View details: first in the "more" and context menus of a row; not on the detail page itself.

    [Fact]
    public void View_details_comes_first_in_the_context_and_more_menus_of_a_container()
    {
        var actions = RowActions.ForContainer(Row("web"), connected: true);

        var viewDetails = Find(actions, RowActionKind.ViewDetails);
        Assert.Same(viewDetails, actions.Sections[0].Single());
        Assert.Same(viewDetails, actions.MoreMenu(compact: false)[0].Single());
        Assert.Same(viewDetails, actions.MoreMenu(compact: true)[0].Single());
        Assert.DoesNotContain(viewDetails, actions.Buttons(compact: false));
        Assert.Equal("View details", viewDetails.Label);
        Assert.Null(viewDetails.EngineAction);
        Assert.Equal<string>(["web"], viewDetails.Ids);
    }

    [Fact]
    public void View_details_is_enabled_while_not_connected_or_busy()
    {
        Assert.True(Find(RowActions.ForContainer(Row("web"), connected: false), RowActionKind.ViewDetails).IsEnabled);
        Assert.True(Find(RowActions.ForContainer(Row("web", pending: ContainerAction.Stop), connected: true), RowActionKind.ViewDetails).IsEnabled);
    }

    [Fact]
    public void The_detail_page_has_the_same_actions_without_view_details()
    {
        var row = Row("web", published: true);
        var list = RowActions.ForContainer(row, connected: true);
        var page = RowActions.ForContainer(row, connected: true, viewDetails: false);

        static object Shape(RowAction a) => (a.Kind, a.Label, a.IsEnabled, a.Button, a.IsMain, a.EngineAction, string.Join(",", a.Ids), a.Description, a.RunCommandShell);
        Assert.Equal(list.Sections.Skip(1).Select(s => s.Select(Shape).ToArray()), page.Sections.Select(s => s.Select(Shape).ToArray()));
        Assert.Equal(list.Buttons(compact: false).Select(Shape), page.Buttons(compact: false).Select(Shape));
        Assert.Equal(list.BrowserUrl, page.BrowserUrl);
    }

    [Fact]
    public void A_project_has_no_view_details() =>
        Assert.DoesNotContain(
            RowActions.ForProject([Row("a")], connected: true).Sections.SelectMany(s => s),
            a => a.Kind == RowActionKind.ViewDetails);

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

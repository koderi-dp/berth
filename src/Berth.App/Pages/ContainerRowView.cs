using Berth.Components;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using static Microsoft.UI.Reactor.Factories;
using MenuEntry = Microsoft.UI.Reactor.Core.MenuFlyoutItemBase;

namespace Berth.Pages;

/// <summary>What the rows of the containers table call back. Built by the page on every render.</summary>
/// <param name="Send">Sends an action for the given container ids to the engine.</param>
/// <param name="SetChecked">Checks (true) or unchecks the given container ids.</param>
/// <param name="ToggleGroup">Collapses or expands the compose project of that name.</param>
/// <param name="RequestDelete">Asks to delete the given rows (the page confirms first).</param>
internal sealed record ContainerListCallbacks(
    Action<IReadOnlyList<string>, ContainerAction> Send,
    Action<IEnumerable<string>, bool> SetChecked,
    Action<string> ToggleGroup,
    Action<IReadOnlyList<ContainerRow>> RequestDelete);

/// <summary>
/// The rows of the containers table: the header row, a container row and a compose project row. All of them lay out their cells in
/// the same <see cref="WideColumns"/> or <see cref="CompactColumns"/>, so the columns line up.
/// </summary>
internal static class ContainerRowView
{
    /// <summary>
    /// Checkbox, chevron (compose project rows only), state dot, name, image, status, ports, created, actions. The dot has its own
    /// column so a project's dot and its containers' dots sit under each other, right of the chevron.
    /// </summary>
    private static readonly GridSize[] WideColumns =
    [
        GridSize.Px(40),
        GridSize.Px(28),
        GridSize.Px(24),
        GridSize.Star(4), // the name: wider than the others, a project's containers are indented in it
        GridSize.Star(3),
        GridSize.Star(3),
        GridSize.Px(120), // ports: wide enough for "65535:65535"
        GridSize.Px(120),
        GridSize.Px(236), // four buttons, the menu, a divider and Delete
    ];

    /// <summary>The same columns for a narrow window: Restart and Pause move into a menu, so the actions need less room.</summary>
    private static readonly GridSize[] CompactColumns =
    [
        GridSize.Px(40),
        GridSize.Px(28),
        GridSize.Px(24),
        GridSize.Star(4), // the name: wider than the others, a project's containers are indented in it
        GridSize.Star(3),
        GridSize.Star(3),
        GridSize.Px(120), // ports: wide enough for "65535:65535"
        GridSize.Px(120),
        GridSize.Px(148), // one button, the menu, a divider and Delete
    ];

    /// <summary>The window is at least this wide (in effective pixels) when every action gets its own button.</summary>
    public const double WideLayoutMinWidth = 1300;

    private static readonly GridSize[] OneRow = [GridSize.Star()];

    /// <summary>Every row is this tall at least, so a project row and a container row (two lines) are the same height.</summary>
    private const double RowHeight = 52;

    /// <summary>How far a compose project's rows are indented under it.</summary>
    private const double Indent = 20;

    /// <summary>How many ports a row shows before it sums up the rest.</summary>
    private const int ShownPorts = 2;

    /// <summary>
    /// The column titles, with a checkbox that checks or clears every row shown (<paramref name="checkedState"/> null: some are).
    /// Like every checkbox and action of the table, it is disabled while not <paramref name="connected"/>.
    /// </summary>
    public static Element Header(bool? checkedState, bool connected, bool compact, Action<bool> onCheckAll) =>
        Cells(
            minHeight: 36,
            compact,
            SelectBox(checkedState, onCheckAll, "Select all").IsEnabled(connected).Grid(column: 0),
            ColumnTitle("Name").Grid(column: 3),
            ColumnTitle("Image").Grid(column: 4),
            ColumnTitle("Status").Grid(column: 5),
            ColumnTitle("Ports").Grid(column: 6),
            ColumnTitle("Created").Grid(column: 7));

    /// <summary>
    /// One container. <paramref name="project"/> not null: it belongs to that compose project and sits under the project's row,
    /// indented and named without the project in front (the tooltip keeps the whole name). Its checkbox
    /// and actions are disabled while not <paramref name="connected"/>; its actions also while one is in progress on it.
    /// </summary>
    public static Element Row(ContainerRow row, string? project, bool isChecked, bool connected, bool compact, ContainerListCallbacks callbacks)
    {
        var indented = project is not null;
        var shownName = project is null ? row.Name : ComposeGrouping.NameInProject(row.Name, project);
        var busy = row.Pending is not null;
        var canAct = BulkSelection.CanAct(row, connected);
        var stopped = row.State == RowState.Stopped;
        string[] ids = [row.Id];

        // The row's main action, in the accent colour: Stop for a container that is active, Start for one that is not.
        var startOrStop = stopped
            ? Widgets.IconButton(Glyphs.Play, "Start", () => callbacks.Send(ids, ContainerAction.Start), color: Primary(canAct)).AutomationId("start-" + row.Name)
            : Widgets.IconButton(Glyphs.Stop, "Stop", () => callbacks.Send(ids, ContainerAction.Stop), color: Primary(canAct)).AutomationId("stop-" + row.Name);

        // Restart applies to any container; pause only to a running one, and a paused one gets Resume in its place. In a wide
        // window each is a button on the row; in a narrow one (compact) they go first in the "more" menu, so the other columns
        // keep their room. The menu is on the row at every width: it also holds what never gets a button.
        var paused = row.State == RowState.Paused;
        var canPause = row.State == RowState.Running;
        void Restart() => callbacks.Send(ids, ContainerAction.Restart);
        void PauseOrResume() => callbacks.Send(ids, paused ? ContainerAction.Unpause : ContainerAction.Pause);

        var browserUrl = row.Ports.FirstOrDefault(p => p.Url is not null)?.Url;
        MenuEntry?[] menu =
        [
            compact ? MenuItem("Restart", Restart, "Refresh") : null,
            compact && paused ? MenuItem("Resume", PauseOrResume, "Play") : null,
            compact && !paused ? MenuItem("Pause", PauseOrResume, "Pause") with { IsEnabled = canPause } : null,
            compact ? MenuSeparator() : null,
            browserUrl is null ? null : MenuItem("Open in browser", () => OpenInBrowser(browserUrl), "Globe"),
            MenuItem("Copy name", () => CopyToClipboard(row.Name), "Copy"),
            MenuItem("Copy ID", () => CopyToClipboard(row.Id), "Copy"),
        ];
        var more = Widgets.IconButton(Glyphs.More, "More actions", null)
            .AutomationId("more-" + row.Name)
            .WithFlyout(MenuItems([.. menu.OfType<MenuEntry>()]));

        Element?[] buttons = compact
            ? [startOrStop.IsEnabled(canAct), more]
            :
            [
                startOrStop.IsEnabled(canAct),
                Widgets.IconButton(Glyphs.Restart, "Restart", Restart).AutomationId("restart-" + row.Name).IsEnabled(canAct),
                paused
                    ? Widgets.IconButton(Glyphs.Resume, "Resume", PauseOrResume).AutomationId("resume-" + row.Name).IsEnabled(canAct)
                    : Widgets.IconButton(Glyphs.Pause, "Pause", PauseOrResume).AutomationId("pause-" + row.Name).IsEnabled(canAct && canPause),
                more,
            ];

        var delete = DeleteButton(() => callbacks.RequestDelete((ContainerRow[])[row]), canAct).AutomationId("delete-" + row.Name);

        // A container that ended badly stands out; one that just is not running steps back.
        ThemeRef? statusColor = ContainerStatus.IsFailedExit(row.Status) ? Theme.SystemCritical : stopped ? Theme.SecondaryText : null;

        var name = (FlexColumn(
                OneLine(Body(shownName)).ToolTip(row.Name),
                (FlexRow(
                        Caption(row.ShortId).Foreground(Theme.SecondaryText),
                        Widgets.IconButton(Glyphs.Copy, "Copy ID", () => CopyToClipboard(row.Id), size: 12)
                            .Padding(4)
                            .MinWidth(24)
                            .MinHeight(24))
                    with
                    {
                        AlignItems = FlexAlign.Center,
                        ColumnGap = 2,
                    }))
            with
            {
                JustifyContent = FlexJustify.Center,
            })
            .Margin(indented ? Indent : 0, 0, 12, 0);

        return Cells(
            minHeight: RowHeight,
            compact,
            SelectBox(isChecked, value => callbacks.SetChecked(ids, value), "Select " + row.Name).IsEnabled(connected).Grid(column: 0),
            StateMark(ContainerStatus.Shape(row.State), StateColor(row.State), row.State.ToString(), busy).Grid(column: 2),
            name.Grid(column: 3),
            CellText(row.Image).Grid(column: 4),
            CellText(row.Status, statusColor).Grid(column: 5),
            Ports(row.Ports).Grid(column: 6),
            CellText(AgeFormat.Ago(DateTimeOffset.UtcNow, row.Created)).Grid(column: 7),
            Actions([.. buttons, ActionDivider(), delete]).Grid(column: 8));
    }

    /// <summary>
    /// A compose project: a chevron that collapses or expands it, its state, its name, and actions for all its rows shown: Start sends
    /// the stopped ones, Stop the others, Delete all of them. Its checkbox and actions are disabled while not
    /// <paramref name="connected"/>; its actions also while one is in progress on any of its rows.
    /// </summary>
    public static Element Group(ContainerGroup group, bool collapsed, bool? checkedState, bool connected, bool compact, ContainerListCallbacks callbacks)
    {
        var project = group.Project ?? throw new ArgumentException("A group row needs a compose project.", nameof(group));
        var rows = group.Rows;
        var canAct = BulkSelection.CanAct(rows, connected);
        string[] stopped = [.. rows.Where(r => r.State == RowState.Stopped).Select(r => r.Id)];
        string[] running = [.. rows.Where(r => r.State != RowState.Stopped).Select(r => r.Id)];

        var chevron = Widgets.IconButton(
                collapsed ? Glyphs.ChevronRight : Glyphs.ChevronDown,
                collapsed ? "Expand" : "Collapse",
                () => callbacks.ToggleGroup(project),
                size: 12)
            .AutomationId("toggle-" + project)
            .Padding(6)
            .MinWidth(0)
            .MinHeight(0);

        var name = OneLine(BodyStrong(project)).ToolTip(project).VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0);

        string[] all = [.. rows.Select(r => r.Id)];
        void Start() => callbacks.Send(stopped, ContainerAction.Start);
        void Stop() => callbacks.Send(running, ContainerAction.Stop);
        void Restart() => callbacks.Send(all, ContainerAction.Restart);

        // The main action is Stop while anything in the project is active, Start otherwise. The other one (a project can have
        // both stopped and active containers) and Restart are buttons in a wide window and menu items in a narrow one.
        var stopIsMain = running.Length > 0;
        var main = stopIsMain
            ? Widgets.IconButton(Glyphs.Stop, "Stop", Stop, color: Primary(canAct)).AutomationId("stop-" + project)
            : Widgets.IconButton(Glyphs.Play, "Start", Start, color: Primary(canAct)).AutomationId("start-" + project);
        var alsoStart = stopIsMain && stopped.Length > 0;

        Element?[] buttons = compact
            ?
            [
                main.IsEnabled(canAct),
                Widgets.IconButton(Glyphs.More, "More actions", null)
                    .AutomationId("more-" + project)
                    .WithFlyout(MenuItems(
                    [
                        .. alsoStart ? (MenuEntry[])[MenuItem("Start", Start, "Play")] : [],
                        MenuItem("Restart", Restart, "Refresh"),
                    ]))
                    .IsEnabled(canAct),
            ]
            :
            [
                alsoStart ? Widgets.IconButton(Glyphs.Play, "Start", Start).AutomationId("start-" + project).IsEnabled(canAct) : null,
                main.IsEnabled(canAct),
                Widgets.IconButton(Glyphs.Restart, "Restart", Restart).AutomationId("restart-" + project).IsEnabled(canAct),
            ];
        var delete = DeleteButton(() => callbacks.RequestDelete(rows), canAct).AutomationId("delete-" + project);

        return Cells(
            minHeight: RowHeight,
            compact,
            SelectBox(checkedState, value => callbacks.SetChecked(all, value), "Select " + project).IsEnabled(connected).Grid(column: 0),
            chevron.Grid(column: 1),
            StateMark(ContainerStatus.GroupShape(rows.Select(r => r.State).ToArray()), StateColor(group.State), group.State.ToString(), busy: false).Grid(column: 2),
            name.Grid(column: 3, columnSpan: 5),
            Actions([.. buttons, ActionDivider(), delete]).Grid(column: 8));
    }

    /// <summary>The accent colour for a row's main action while it can act; none while it cannot, so the disabled look shows.</summary>
    private static ThemeRef? Primary(bool canAct) => canAct ? Theme.AccentText : null;

    /// <summary>Delete is red and, with <see cref="ActionDivider"/> before it, stands apart from the actions that lose nothing.</summary>
    private static ButtonElement DeleteButton(Action onClick, bool canAct) =>
        Widgets.IconButton(Glyphs.Delete, "Delete", onClick, color: canAct ? Theme.SystemCritical : null).IsEnabled(canAct);

    private static Element ActionDivider() =>
        Border(Empty()).Background(Theme.DividerStroke).Size(1, 20).Margin(4, 0, 4, 0).Flex(shrink: 0);

    /// <summary>Running: the success colour; paused or restarting: caution; stopped: secondary text.</summary>
    private static ThemeRef StateColor(RowState state) => state switch
    {
        RowState.Running => Theme.SystemSuccess,
        RowState.Paused or RowState.Restarting => Theme.SystemCaution,
        _ => Theme.SecondaryText,
    };

    /// <summary>The state as a shape in the state's colour (so it does not rest on colour alone), or a ring while an action runs.</summary>
    private static Element StateMark(StateShape shape, ThemeRef color, string label, bool busy) =>
        busy
            ? ProgressRing().Size(16, 16).AutomationName("Working").HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Center)
            : Widgets.StateMark(shape, color).AutomationName(label).ToolTip(label).HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Center);

    private static Element Cells(double minHeight, bool compact, params Element[] cells) =>
        Border(Grid(compact ? CompactColumns : WideColumns, OneRow, cells).MinHeight(minHeight).Padding(12, 0, 8, 0))
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 0, 0, 1);

    private static Element SelectBox(bool? checkedState, Action<bool> onChanged, string label) =>
        CheckBox(checkedState, onChanged)
            .AutomationName(label)
            .MinWidth(0)
            .VAlign(VerticalAlignment.Center);

    private static Element ColumnTitle(string text) =>
        Caption(text).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center);

    /// <summary>One line of text that ends in an ellipsis when it does not fit (the body style wraps by default, which would make rows uneven).</summary>
    private static TextBlockElement OneLine(TextBlockElement text) =>
        text.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis);

    private static Element CellText(string text, ThemeRef? color = null) =>
        (color is { } drawn ? OneLine(Body(text)).Foreground(drawn) : OneLine(Body(text)))
            .ToolTip(text)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 12, 0);

    private static Element Actions(params Element?[] buttons) =>
        (FlexRow(buttons) with
        {
            AlignItems = FlexAlign.Center,
            JustifyContent = FlexJustify.FlexEnd,
            ColumnGap = 4,
        })
        .VAlign(VerticalAlignment.Center);

    /// <summary>The first ports, each a link when it has a URL to open, then "+n" for the rest (listed in its tooltip).</summary>
    private static Element Ports(IReadOnlyList<PortLink> ports)
    {
        var shown = ports.Take(ShownPorts).Select(PortCell);
        var rest = ports.Count > ShownPorts
            ? Caption("+" + (ports.Count - ShownPorts)).Foreground(Theme.SecondaryText).ToolTip(string.Join(", ", ports.Skip(ShownPorts).Select(p => p.Text)))
            : null;

        return (FlexRow([.. shown, rest]) with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 4,
            })
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 12, 0);
    }

    private static Element PortCell(PortLink port) =>
        port.Url is { } url
            ? HyperlinkButton(port.Text, url).Padding(4, 2).ToolTip(url.ToString())
            : OneLine(Body(port.Text));

    /// <summary>Opens a published port in the default browser. Fire and forget: nothing waits on the browser.</summary>
    private static void OpenInBrowser(Uri url) => _ = Windows.System.Launcher.LaunchUriAsync(url);

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}

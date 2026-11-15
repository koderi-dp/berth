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
    /// Like every checkbox and action of the table, it is disabled while not <paramref name="connected"/>. Every title but Ports is a
    /// button that sorts by its column (<paramref name="onSort"/>); the sorted one shows an arrow.
    /// </summary>
    public static Element Header(bool? checkedState, bool connected, bool compact, Action<bool> onCheckAll, ContainerSort sort, Action<SortColumn> onSort) =>
        Cells(
            minHeight: 36,
            compact,
            SelectBox(checkedState, onCheckAll, "Select all").IsEnabled(connected).Grid(column: 0),
            SortTitle(SortColumn.Name, sort, onSort).Grid(column: 3),
            SortTitle(SortColumn.Image, sort, onSort).Grid(column: 4),
            SortTitle(SortColumn.Status, sort, onSort).Grid(column: 5),
            ColumnTitle("Ports").Grid(column: 6),
            SortTitle(SortColumn.Created, sort, onSort).Grid(column: 7));

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
        var stopped = row.State == RowState.Stopped;
        string[] ids = [row.Id];

        var actions = RowActions.ForContainer(row, connected);
        void Run(RowAction action)
        {
            switch (action.Kind)
            {
                case RowActionKind.OpenInBrowser when actions.BrowserUrl is { } url:
                    OpenInBrowser(url);
                    break;
                case RowActionKind.CopyName:
                    CopyToClipboard(row.Name);
                    break;
                case RowActionKind.CopyId:
                    CopyToClipboard(row.Id);
                    break;
                default:
                    RunEngineAction(action, (ContainerRow[])[row], callbacks);
                    break;
            }
        }

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
            ActionButtons(actions, compact, row.Name, Run).Grid(column: 8))
            .WithContextFlyout(ContextMenu(actions, Run));
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
        string[] all = [.. rows.Select(r => r.Id)];

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

        var actions = RowActions.ForProject(rows, connected);
        void Run(RowAction action) => RunEngineAction(action, rows, callbacks);

        return Cells(
            minHeight: RowHeight,
            compact,
            SelectBox(checkedState, value => callbacks.SetChecked(all, value), "Select " + project).IsEnabled(connected).Grid(column: 0),
            chevron.Grid(column: 1),
            StateMark(ContainerStatus.GroupShape(rows.Select(r => r.State).ToArray()), StateColor(group.State), group.State.ToString(), busy: false).Grid(column: 2),
            name.Grid(column: 3, columnSpan: 5),
            ActionButtons(actions, compact, project, Run).Grid(column: 8))
            .WithContextFlyout(ContextMenu(actions, Run));
    }

    /// <summary>Sends an engine action for its containers, or asks to delete <paramref name="rows"/> (the page confirms first).</summary>
    private static void RunEngineAction(RowAction action, IReadOnlyList<ContainerRow> rows, ContainerListCallbacks callbacks)
    {
        if (action.Kind == RowActionKind.Delete)
            callbacks.RequestDelete(rows);
        else if (action.EngineAction is { } engineAction)
            callbacks.Send(action.Ids, engineAction);
    }

    /// <summary>
    /// The row's buttons: the actions that are buttons at this width (the main one in the accent colour), the "more" menu with the
    /// rest when there is any, a divider and Delete. AutomationIds are the action and the row's name: <c>stop-web-1</c>, <c>more-web-1</c>.
    /// </summary>
    private static Element ActionButtons(RowActions actions, bool compact, string name, Action<RowAction> run)
    {
        var buttons = actions.Buttons(compact)
            .Select(a => Widgets.IconButton(Glyph(a.Kind), a.Label, () => run(a), color: a.IsMain ? Primary(a.IsEnabled) : null)
                .AutomationId(AutomationPrefix(a.Kind) + name)
                .IsEnabled(a.IsEnabled));

        var menu = actions.MoreMenu(compact);
        var more = menu.Count == 0
            ? null
            : Widgets.IconButton(Glyphs.More, "More actions", null)
                .AutomationId("more-" + name)
                .WithFlyout(MenuItems(MenuEntries(menu, run)))
                .IsEnabled(menu.Any(s => s.Any(a => a.IsEnabled)));

        var delete = actions.Delete;
        var deleteButton = Widgets.IconButton(Glyphs.Delete, delete.Label, () => run(delete), color: delete.IsEnabled ? Theme.SystemCritical : null)
            .AutomationId("delete-" + name)
            .IsEnabled(delete.IsEnabled);

        return Actions([.. buttons, more, ActionDivider(), deleteButton]);
    }

    /// <summary>The right-click menu: every action of the row in order, its sections apart.</summary>
    private static Element ContextMenu(RowActions actions, Action<RowAction> run) =>
        MenuItems(MenuEntries(actions.Sections, run));

    private static MenuEntry[] MenuEntries(IReadOnlyList<IReadOnlyList<RowAction>> sections, Action<RowAction> run) =>
    [
        .. sections.SelectMany((section, i) => (MenuEntry[])
        [
            .. i > 0 ? (MenuEntry[])[MenuSeparator()] : [],
            .. section.Select(a => MenuItem(a.Label, () => run(a)) with { IconElement = Widgets.MenuGlyph(Glyph(a.Kind)), IsEnabled = a.IsEnabled }),
        ]),
    ];

    /// <summary>The glyph of an action, the same on its button and in the menus (the solid Stop: the outline one looks like a checkbox).</summary>
    private static string Glyph(RowActionKind kind) => kind switch
    {
        RowActionKind.Start => Glyphs.Play,
        RowActionKind.Stop => Glyphs.Stop,
        RowActionKind.Restart => Glyphs.Restart,
        RowActionKind.Pause => Glyphs.Pause,
        RowActionKind.Resume => Glyphs.Resume,
        RowActionKind.Delete => Glyphs.Delete,
        RowActionKind.OpenInBrowser => Glyphs.Globe,
        _ => Glyphs.Copy,
    };

    private static string AutomationPrefix(RowActionKind kind) => kind.ToString().ToLowerInvariant() + "-";

    /// <summary>The accent colour for a row's main action while it can act; none while it cannot, so the disabled look shows.</summary>
    private static ThemeRef? Primary(bool canAct) => canAct ? Theme.AccentText : null;

    /// <summary>Sets Delete, in red, apart from the actions that lose nothing.</summary>
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

    /// <summary>
    /// A column title as a borderless button: its text, and an arrow while the table is sorted by it (up ascending, down
    /// descending). Its accessible name says the state ("Name, sorted ascending"). The negative margin keeps the text in line with
    /// the column's cells while the button keeps its padding for the hover background.
    /// </summary>
    private static Element SortTitle(SortColumn column, ContainerSort sort, Action<SortColumn> onSort)
    {
        var sorted = sort.Column == column;
        // A stack, not a flex row: a flex row takes the whole width it is offered, which would stretch the button over the cell.
        var content = HStack(
            4,
            Caption(column.ToString()).Foreground(sorted ? Theme.PrimaryText : Theme.SecondaryText).VAlign(VerticalAlignment.Center),
            sorted ? Widgets.Glyph(sort.Descending ? Glyphs.ArrowDown : Glyphs.ArrowUp, size: 12, color: Theme.PrimaryText).VAlign(VerticalAlignment.Center) : Empty());

        var button = Button(content, () => onSort(column))
            .SubtleButton()
            .AutomationId("sort-" + column.ToString().ToLowerInvariant())
            .AutomationName(sort.AccessibleName(column))
            .Padding(8, 4, 8, 4);

        return button.Margin(-8, 0, 0, 0).HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Center);
    }

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

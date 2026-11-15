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
        GridSize.Px(172), // four buttons
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
        GridSize.Px(124), // two buttons and the menu
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

        var startOrStop = stopped
            ? Widgets.IconButton(Glyphs.Play, "Start", () => callbacks.Send(ids, ContainerAction.Start)).AutomationId("start-" + row.Name)
            : Widgets.IconButton(Glyphs.Stop, "Stop", () => callbacks.Send(ids, ContainerAction.Stop)).AutomationId("stop-" + row.Name);

        // Restart applies to any container; pause only to a running one, and a paused one gets Resume in its place. In a wide
        // window each is a button on the row; in a narrow one (compact) they sit in a "more" menu, so the other columns keep their room.
        var paused = row.State == RowState.Paused;
        var canPause = row.State == RowState.Running;
        void Restart() => callbacks.Send(ids, ContainerAction.Restart);
        void PauseOrResume() => callbacks.Send(ids, paused ? ContainerAction.Unpause : ContainerAction.Pause);
        Element[] secondary = compact
            ?
            [
                Widgets.IconButton(Glyphs.More, "More actions", null)
                    .AutomationId("more-" + row.Name)
                    .WithFlyout(MenuItems(
                        MenuItem("Restart", Restart, "Refresh"),
                        paused
                            ? MenuItem("Resume", PauseOrResume, "Play")
                            : MenuItem("Pause", PauseOrResume, "Pause") with { IsEnabled = canPause }))
                    .IsEnabled(canAct),
            ]
            :
            [
                Widgets.IconButton(Glyphs.Restart, "Restart", Restart).AutomationId("restart-" + row.Name).IsEnabled(canAct),
                paused
                    ? Widgets.IconButton(Glyphs.Resume, "Resume", PauseOrResume).AutomationId("resume-" + row.Name).IsEnabled(canAct)
                    : Widgets.IconButton(Glyphs.Pause, "Pause", PauseOrResume).AutomationId("pause-" + row.Name).IsEnabled(canAct && canPause),
            ];

        var delete = Widgets.IconButton(Glyphs.Delete, "Delete", () => callbacks.RequestDelete((ContainerRow[])[row])).AutomationId("delete-" + row.Name);

        var name = (FlexColumn(
                OneLine(Body(shownName)).ToolTip(row.Name),
                (FlexRow(
                        Caption(row.ShortId).Foreground(Theme.SecondaryText),
                        Widgets.IconButton(Glyphs.Copy, "Copy ID", () => CopyToClipboard(row.Id), size: 12)
                            .Padding(4)
                            .MinWidth(0)
                            .MinHeight(0))
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
            StateMark(row.State, busy).Grid(column: 2),
            name.Grid(column: 3),
            CellText(row.Image).Grid(column: 4),
            CellText(row.Status).Grid(column: 5),
            Ports(row.Ports).Grid(column: 6),
            CellText(AgeFormat.Ago(DateTimeOffset.UtcNow, row.Created)).Grid(column: 7),
            Actions([startOrStop.IsEnabled(canAct), .. secondary, delete.IsEnabled(canAct)]).Grid(column: 8));
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

        var start = stopped.Length > 0
            ? Widgets.IconButton(Glyphs.Play, "Start", () => callbacks.Send(stopped, ContainerAction.Start)).AutomationId("start-" + project).IsEnabled(canAct)
            : null;
        var stop = running.Length > 0
            ? Widgets.IconButton(Glyphs.Stop, "Stop", () => callbacks.Send(running, ContainerAction.Stop)).AutomationId("stop-" + project).IsEnabled(canAct)
            : null;
        var delete = Widgets.IconButton(Glyphs.Delete, "Delete", () => callbacks.RequestDelete(rows)).AutomationId("delete-" + project).IsEnabled(canAct);

        return Cells(
            minHeight: RowHeight,
            compact,
            SelectBox(checkedState, value => callbacks.SetChecked(rows.Select(r => r.Id), value), "Select " + project).IsEnabled(connected).Grid(column: 0),
            chevron.Grid(column: 1),
            StateMark(group.State, busy: false).Grid(column: 2),
            name.Grid(column: 3, columnSpan: 5),
            Actions(start, stop, delete).Grid(column: 8));
    }

    /// <summary>Running: the success colour; paused or restarting: caution; stopped: secondary text.</summary>
    private static ThemeRef StateColor(RowState state) => state switch
    {
        RowState.Running => Theme.SystemSuccess,
        RowState.Paused or RowState.Restarting => Theme.SystemCaution,
        _ => Theme.SecondaryText,
    };

    private static Element StateMark(RowState state, bool busy) =>
        busy
            ? ProgressRing().Size(16, 16).AutomationName("Working").HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Center)
            : Widgets.StatusDot(StateColor(state)).AutomationName(state.ToString()).HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Center);

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

    private static Element CellText(string text) =>
        OneLine(Body(text))
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

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}

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
/// the same <see cref="Columns"/>, so the columns line up.
/// </summary>
internal static class ContainerRowView
{
    /// <summary>Checkbox, state dot, name, image, status, ports, created, actions.</summary>
    private static readonly GridSize[] Columns =
    [
        GridSize.Px(40),
        GridSize.Px(28),
        GridSize.Star(3),
        GridSize.Star(3),
        GridSize.Star(3),
        GridSize.Star(2),
        GridSize.Px(110),
        GridSize.Px(124),
    ];

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
    public static Element Header(bool? checkedState, bool connected, Action<bool> onCheckAll) =>
        Cells(
            minHeight: 36,
            SelectBox(checkedState, onCheckAll, "Select all").IsEnabled(connected).Grid(column: 0),
            ColumnTitle("Name").Grid(column: 2),
            ColumnTitle("Image").Grid(column: 3),
            ColumnTitle("Status").Grid(column: 4),
            ColumnTitle("Ports").Grid(column: 5),
            ColumnTitle("Created").Grid(column: 6));

    /// <summary>
    /// One container. <paramref name="indented"/>: it belongs to a compose project and sits under the project's row. Its checkbox
    /// and actions are disabled while not <paramref name="connected"/>; its actions also while one is in progress on it.
    /// </summary>
    public static Element Row(ContainerRow row, bool indented, bool isChecked, bool connected, ContainerListCallbacks callbacks)
    {
        var busy = row.Pending is not null;
        var canAct = BulkSelection.CanAct(row, connected);
        var stopped = row.State == RowState.Stopped;
        string[] ids = [row.Id];

        var startOrStop = stopped
            ? Widgets.IconButton(Glyphs.Play, "Start", () => callbacks.Send(ids, ContainerAction.Start)).AutomationId("start-" + row.Name)
            : Widgets.IconButton(Glyphs.Stop, "Stop", () => callbacks.Send(ids, ContainerAction.Stop)).AutomationId("stop-" + row.Name);

        // Pause and resume only apply to a running or a paused container; restart to any.
        var pauseOrResume = row.State == RowState.Paused
            ? MenuItem("Resume", () => callbacks.Send(ids, ContainerAction.Unpause), "Play")
            : MenuItem("Pause", () => callbacks.Send(ids, ContainerAction.Pause), "Pause") with { IsEnabled = row.State == RowState.Running };
        var more = Widgets.IconButton(Glyphs.More, "More actions", null)
            .AutomationId("more-" + row.Name)
            .WithFlyout(MenuItems(
                MenuItem("Restart", () => callbacks.Send(ids, ContainerAction.Restart), "Refresh"),
                pauseOrResume));

        var delete = Widgets.IconButton(Glyphs.Delete, "Delete", () => callbacks.RequestDelete((ContainerRow[])[row])).AutomationId("delete-" + row.Name);

        var name = (FlexColumn(
                OneLine(Body(row.Name)).ToolTip(row.Name),
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
            SelectBox(isChecked, value => callbacks.SetChecked(ids, value), "Select " + row.Name).IsEnabled(connected).Grid(column: 0),
            StateMark(row.State, busy).Grid(column: 1),
            name.Grid(column: 2),
            CellText(row.Image).Grid(column: 3),
            CellText(row.Status).Grid(column: 4),
            Ports(row.Ports).Grid(column: 5),
            CellText(AgeFormat.Ago(DateTimeOffset.UtcNow, row.Created)).Grid(column: 6),
            Actions(startOrStop.IsEnabled(canAct), more.IsEnabled(canAct), delete.IsEnabled(canAct)).Grid(column: 7));
    }

    /// <summary>
    /// A compose project: a chevron that collapses or expands it, its state, its name, and actions for all its rows shown: Start sends
    /// the stopped ones, Stop the others, Delete all of them. Its checkbox and actions are disabled while not
    /// <paramref name="connected"/>; its actions also while one is in progress on any of its rows.
    /// </summary>
    public static Element Group(ContainerGroup group, bool collapsed, bool? checkedState, bool connected, ContainerListCallbacks callbacks)
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

        var name = (FlexRow(
                Widgets.StatusDot(StateColor(group.State)),
                OneLine(BodyStrong(project)).ToolTip(project))
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 8,
            })
            .Margin(0, 0, 12, 0);

        var start = stopped.Length > 0
            ? Widgets.IconButton(Glyphs.Play, "Start", () => callbacks.Send(stopped, ContainerAction.Start)).AutomationId("start-" + project).IsEnabled(canAct)
            : null;
        var stop = running.Length > 0
            ? Widgets.IconButton(Glyphs.Stop, "Stop", () => callbacks.Send(running, ContainerAction.Stop)).AutomationId("stop-" + project).IsEnabled(canAct)
            : null;
        var delete = Widgets.IconButton(Glyphs.Delete, "Delete", () => callbacks.RequestDelete(rows)).AutomationId("delete-" + project).IsEnabled(canAct);

        return Cells(
            minHeight: RowHeight,
            SelectBox(checkedState, value => callbacks.SetChecked(rows.Select(r => r.Id), value), "Select " + project).IsEnabled(connected).Grid(column: 0),
            chevron.Grid(column: 1),
            name.Grid(column: 2, columnSpan: 5),
            Actions(start, stop, delete).Grid(column: 7));
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

    private static Element Cells(double minHeight, params Element[] cells) =>
        Grid(Columns, OneRow, cells).MinHeight(minHeight).Padding(12, 0, 8, 0);

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

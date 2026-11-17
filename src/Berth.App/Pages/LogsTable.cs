using System.Collections;
using System.Collections.Immutable;
using Berth.Components;
using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>
/// What the Logs page's rows call back. One instance per page, kept across renders; the page points its members at its latest handlers on
/// every render.
/// </summary>
internal sealed class LogsRowHandlers
{
    /// <summary>Checks or unchecks the line with this sequence number.</summary>
    public Action<long, bool> SetChecked { get; set; } = (_, _) => { };

    /// <summary>Expands or collapses the line with this sequence number.</summary>
    public Action<long> ToggleExpanded { get; set; } = _ => { };

    /// <summary>Opens the container's page.</summary>
    public Action<string> OpenContainer { get; set; } = _ => { };
}

/// <summary>How the rows are drawn on this render: the columns shown, the search, the checked and expanded lines, colours and theme.</summary>
internal sealed record LogsRowView(
    bool ShowTime,
    bool ShowObject,
    bool Colors,
    bool Dark,
    LogsQuery Query,
    ImmutableHashSet<long> Checked,
    ImmutableHashSet<long> Expanded,
    ImmutableDictionary<string, LogSource> Sources,
    LogsRowHandlers Handlers);

/// <summary>A shown line and its position, which keys its row (see <see cref="LogsRows"/>).</summary>
internal readonly record struct LogsRow(int Position, LogEntry Entry);

/// <summary>
/// The shown lines as rows, made as the list asks for them. Keyed by position, as the Logs tab's are: once the log is full every batch
/// drops as many lines as it adds, and keys that move with the lines made the list build every row again. A row holds nothing but its
/// line; its checkbox and chevron read the page's sets by the line's sequence number.
/// </summary>
internal sealed class LogsRows(IReadOnlyList<LogEntry> entries) : IReadOnlyList<LogsRow>
{
    public int Count => entries.Count;

    public LogsRow this[int index] => new(index, entries[index]);

    public IEnumerator<LogsRow> GetEnumerator()
    {
        var position = 0;
        foreach (var entry in entries)
            yield return new LogsRow(position++, entry);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>The Logs page's table: the header, a row per line (expanded on request), and their shared column widths.</summary>
internal static class LogsTable
{
    private const double LineHeight = 20;
    private const double LineFontSize = 12;
    private const double HeaderHeight = 36;
    private const double RowHeight = 32;
    private const string IconFont = "Segoe Fluent Icons";

    private static readonly GridSize Check = GridSize.Px(40);
    private static readonly GridSize Chevron = GridSize.Px(32);
    private static readonly GridSize TimeWidth = GridSize.Px(160);
    private static readonly GridSize ObjectWidth = GridSize.Px(200);

    // One array per choice of columns, built once: every row and the header lay out in the same widths, and no row hands its grid a new array.
    private static readonly GridSize[] AllColumns = [Check, Chevron, TimeWidth, ObjectWidth, GridSize.Star()];
    private static readonly GridSize[] NoTime = [Check, Chevron, GridSize.Px(0), ObjectWidth, GridSize.Star()];
    private static readonly GridSize[] NoObject = [Check, Chevron, TimeWidth, GridSize.Px(0), GridSize.Star()];
    private static readonly GridSize[] MessageOnly = [Check, Chevron, GridSize.Px(0), GridSize.Px(0), GridSize.Star()];
    private static readonly GridSize[] OneRow = [GridSize.Star()];
    private static readonly GridSize[] IconAndName = [GridSize.Auto, GridSize.Star()];
    private static readonly IReadOnlyList<LogMatch> NoMatches = [];

    private static readonly Dictionary<(int Index, bool Dark), Brush> ObjectBrushes = [];

    private static GridSize[] Columns(bool showTime, bool showObject) =>
        showTime ? showObject ? AllColumns : NoObject : showObject ? NoTime : MessageOnly;

    /// <summary>The header: the checkbox that checks or clears every shown line, and the titles of the columns shown.</summary>
    public static Element Header(bool showTime, bool showObject, bool? checkedState, bool any, Action<bool> onCheckAll) =>
        Border(Grid(
                Columns(showTime, showObject),
                OneRow,
                ListTable.SelectBox(checkedState, onCheckAll, "Select all lines").IsEnabled(any).AutomationId("logs-select-all").Grid(column: 0),
                showTime ? Title("Timestamp").Grid(column: 2) : null,
                showObject ? Title("Object").Grid(column: 3) : null,
                Title("Message").Grid(column: 4))
            .MinHeight(HeaderHeight)
            .Padding(12, 0, 8, 0))
        .BorderBrush(Theme.DividerStroke)
        .BorderThickness(0, 0, 0, 1);

    private static Element Title(string text) => Caption(text).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center);

    /// <summary>
    /// The lines, one row each, virtualized: only the rows in view are built, each from its line alone. Followed to the bottom through
    /// <paramref name="scroller"/>.
    /// </summary>
    public static Element List(LogsRows rows, LogsRowView view, LogScroller scroller) =>
        LazyVStack(rows, row => row.Position.ToString(System.Globalization.CultureInfo.InvariantCulture), (row, _) => Row(row.Entry, view))
            .Set(list =>
            {
                LogScroller.Configure(list, wrap: true);
                scroller.Attach(list);
            })
            .SetRepeater(repeater =>
            {
                scroller.Repeater = repeater;
                if (repeater.Layout is StackLayout stack)
                    stack.Spacing = 0;
                repeater.HorizontalAlignment = HorizontalAlignment.Stretch;
            })
            .AutomationId("logs-lines")
            .AutomationName("Logs");

    /// <summary>
    /// One line: its checkbox, its chevron, its time, its container (icon and name in the container's colour; a link to its page), and
    /// its message on one line (monospace, in colour, search matches marked). Expanded, the whole message wraps below with its
    /// container, stream and time to the millisecond.
    /// </summary>
    private static Element Row(LogEntry entry, LogsRowView view)
    {
        var sequence = entry.Sequence;
        var line = entry.Line;
        var expanded = view.Expanded.Contains(sequence);
        var matches = view.Query.IsEmpty ? NoMatches : Matches(view.Query.Find(line.Text));
        var look = new LogText.LineLook(line, Timestamps: false, Styled: view.Colors && !line.Spans.IsEmpty);
        var stderr = line.Stream == LogStream.Stderr;
        var handlers = view.Handlers;

        var message = (TextBlock(line.Text) with { LineHeight = LineHeight })
            .FontFamily(LogText.Mono)
            .FontSize(LineFontSize)
            .TextWrapping(TextWrapping.NoWrap)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .Foreground(stderr ? Theme.SystemCaution : Theme.PrimaryText)
            .VAlign(VerticalAlignment.Center)
            .Set(block => LogText.Mark(block, look, matches, 0, matches.Count, 0, -1, view.Dark));

        var grid = Grid(
                Columns(view.ShowTime, view.ShowObject),
                OneRow,
                ListTable.SelectBox(view.Checked.Contains(sequence), on => handlers.SetChecked(sequence, on), "Select line").Grid(column: 0),
                Widgets.IconButton(expanded ? Glyphs.ChevronDown : Glyphs.ChevronRight, expanded ? "Collapse the line" : "Expand the line", () => handlers.ToggleExpanded(sequence), size: 12)
                    .Padding(4)
                    .MinWidth(24)
                    .MinHeight(24)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(column: 1),
                view.ShowTime
                    ? Widgets.OneLine(Body(LogsView.Time(entry))).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Grid(column: 2)
                    : null,
                view.ShowObject ? ObjectCell(entry.Origin, view).Margin(0, 0, 12, 0).Grid(column: 3) : null,
                message.Grid(column: 4))
            .MinHeight(RowHeight);

        Element content = expanded ? VStack(0, grid, Details(entry, look, matches, view)) : grid;
        return Border(content.Padding(12, 0, 8, 0))
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 0, 0, 1);
    }

    /// <summary>The whole message, wrapped and selectable, and its container, stream and time to the millisecond.</summary>
    private static Element Details(LogEntry entry, LogText.LineLook look, IReadOnlyList<LogMatch> matches, LogsRowView view)
    {
        var line = entry.Line;
        var origin = entry.Origin;
        var who = origin.Project is null ? origin.Name : $"{origin.Name} ({origin.Label})";
        return VStack(
                4,
                (TextBlock(line.Text) with { LineHeight = LineHeight })
                    .FontFamily(LogText.Mono)
                    .FontSize(LineFontSize)
                    .TextWrapping(TextWrapping.Wrap)
                    .IsTextSelectionEnabled()
                    .Foreground(line.Stream == LogStream.Stderr ? Theme.SystemCaution : Theme.PrimaryText)
                    .Set(block => LogText.Mark(block, look, matches, 0, matches.Count, 0, -1, view.Dark)),
                Caption($"Container {who} · {LogsView.StreamName(line.Stream)} · {LogsView.FullTime(entry)}")
                    .Foreground(Theme.SecondaryText)
                    .IsTextSelectionEnabled())
            .Margin(72, 0, 12, 12);
    }

    /// <summary>
    /// The container: a small icon (a compose service's or a container's) and its label in the container's own colour, a link to its
    /// page; a removed container's in the secondary colour, marked removed, and not a link.
    /// </summary>
    private static Element ObjectCell(LogOrigin origin, LogsRowView view)
    {
        var removed = view.Sources.TryGetValue(origin.ContainerId, out var source) && source.Removed;
        var glyph = origin.Project is null ? Glyphs.Container : Glyphs.Project;
        if (removed)
        {
            var label = origin.Label + " (removed)";
            return Grid(
                    IconAndName,
                    OneRow,
                    Widgets.Glyph(glyph, 12, Theme.SecondaryText).VAlign(VerticalAlignment.Center).Margin(0, 0, 6, 0).Grid(column: 0),
                    Widgets.OneLine(Body(label)).Foreground(Theme.SecondaryText).ToolTip(label).VAlign(VerticalAlignment.Center).Grid(column: 1))
                .VAlign(VerticalAlignment.Center);
        }

        var brush = ObjectBrush(LogsPalette.Index(origin.ContainerId), view.Dark);
        var link = HyperlinkButton(Widgets.OneLine(Body(origin.Label)).Foreground(brush), onClick: () => view.Handlers.OpenContainer(origin.ContainerId))
            .AutomationId("logs-open-" + origin.Name)
            .AutomationName("Open " + origin.Name)
            .ToolTip(origin.Project is null ? origin.Name : $"{origin.Name} ({origin.Label})")
            .Padding(0)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Center);
        return Grid(
                IconAndName,
                OneRow,
                TextBlock(glyph).FontFamily(IconFont).FontSize(12).Foreground(brush).VAlign(VerticalAlignment.Center).Margin(0, 0, 6, 0).Grid(column: 0),
                link.Grid(column: 1))
            .VAlign(VerticalAlignment.Center);
    }

    /// <summary>The brush of palette colour <paramref name="index"/> in the light or dark theme; one per colour and theme, shared by every row.</summary>
    private static Brush ObjectBrush(int index, bool dark)
    {
        if (!ObjectBrushes.TryGetValue((index, dark), out var brush))
        {
            brush = new SolidColorBrush(LogText.Opaque(LogsPalette.Rgb(index, dark)));
            ObjectBrushes[(index, dark)] = brush;
        }

        return brush;
    }

    private static IReadOnlyList<LogMatch> Matches(IReadOnlyList<(int Start, int Length)> found)
    {
        if (found.Count == 0)
            return NoMatches;
        var matches = new LogMatch[found.Count];
        for (var i = 0; i < found.Count; i++)
            matches[i] = new LogMatch(0, found[i].Start, found[i].Length);
        return matches;
    }
}

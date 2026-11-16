using System.Collections.Immutable;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>
/// One column of a list table: its width in the wide and in the compact layout, and what its header cell shows: the select-all
/// checkbox (<see cref="IsSelect"/>), a title, a title that sorts by <see cref="SortBy"/>, or nothing.
/// </summary>
/// <typeparam name="TSort">The table's sort columns (an enum).</typeparam>
internal sealed record ListColumn<TSort>(GridSize Wide, GridSize Compact) where TSort : struct, Enum
{
    /// <summary>The header's title; null for a column without one (checkbox, state mark, actions).</summary>
    public string? Title { get; init; }

    /// <summary>The sort column a click on the title sorts by; null for a title that does not sort.</summary>
    public TSort? SortBy { get; init; }

    /// <summary>The header holds the checkbox that checks or clears every row shown.</summary>
    public bool IsSelect { get; init; }

    /// <summary>A number column: its title ends where its cells' text ends (see <see cref="ListTable.NumberCellEndMargin"/>).</summary>
    public bool AlignRight { get; init; }

    /// <summary>The column is not there in the compact layout: its width is 0 and the header shows no title for it.</summary>
    public bool WideOnly { get; init; }

    /// <summary>A column as wide in both layouts.</summary>
    public static ListColumn<TSort> Of(GridSize width) => new(width, width);

    /// <summary>A column of the wide layout only.</summary>
    public static ListColumn<TSort> InWideOnly(GridSize width) => new(width, GridSize.Px(0)) { WideOnly = true };

    /// <summary>The checkbox column, first in every table.</summary>
    public static ListColumn<TSort> Select() => new(GridSize.Px(40), GridSize.Px(40)) { IsSelect = true };
}

/// <summary>
/// A table's columns, built once (a static of the page): the header and every row lay out their cells in the same widths, so they
/// cannot disagree, and no row hands its grid a new array.
/// </summary>
internal sealed class ListColumns<TSort> where TSort : struct, Enum
{
    private readonly GridSize[] wide;
    private readonly GridSize[] compact;

    public ListColumns(params ListColumn<TSort>[] columns)
    {
        All = [.. columns];
        wide = [.. columns.Select(c => c.Wide)];
        compact = [.. columns.Select(c => c.Compact)];
    }

    public ImmutableArray<ListColumn<TSort>> All { get; }

    /// <summary>Whether the column at <paramref name="index"/> is there in the wide layout only.</summary>
    public bool IsWideOnly(int index) => index >= 0 && index < All.Length && All[index].WideOnly;

    /// <summary>The column widths of the compact or the wide layout.</summary>
    public GridSize[] Widths(bool isCompact) => isCompact ? compact : wide;
}

/// <summary>
/// How a table is sorted, as its header needs it: the sorted column and direction, what a screen reader says for each title, and
/// what a click on a title does.
/// </summary>
internal sealed record ListSortView<TSort>(TSort Column, bool Descending, Func<TSort, string> AccessibleName, Action<TSort> OnSort) where TSort : struct, Enum;

/// <summary>
/// What a table component calls back. One instance per page, kept across renders, so the table's props stay equal while nothing it
/// shows changed; the page points its members at its latest handlers on every render.
/// </summary>
/// <typeparam name="TRow">The table's row.</typeparam>
/// <typeparam name="TSort">The table's sort columns.</typeparam>
internal class TableHandlers<TRow, TSort> where TSort : struct, Enum
{
    /// <summary>Checks (true) or unchecks the rows with these keys.</summary>
    public Action<IEnumerable<string>, bool> SetChecked { get; set; } = (_, _) => { };

    /// <summary>A click on a column title.</summary>
    public Action<TSort> SortBy { get; set; } = _ => { };

    /// <summary>A row's action, from its buttons, its "more" menu or its right-click menu.</summary>
    public Action<TRow, RowAction> Act { get; set; } = (_, _) => { };
}

internal static class ListSortView
{
    /// <summary>The header's view of an images, volumes or networks sort; a click calls <paramref name="onSort"/> with the column.</summary>
    public static ListSortView<TSort> Of<TSort>(ListSort<TSort> sort, Action<TSort> onSort) where TSort : struct, Enum =>
        new(sort.Column, sort.Descending, sort.AccessibleName, onSort);
}

/// <summary>
/// The table of a list page: the header row built from <see cref="ListColumns{TSort}"/>, rows laid out in the same columns with
/// a divider under each, the virtualized list that holds them, and the loading and empty states that take the table's place.
/// </summary>
internal static class ListTable
{
    /// <summary>The header row's height.</summary>
    private const double HeaderHeight = 36;

    /// <summary>A number cell's text ends this far from its column's right edge, so there is a gap before the next column.</summary>
    public const double NumberCellEndMargin = 28;

    /// <summary>
    /// The header: the select-all checkbox (<paramref name="checkedState"/> null: some rows are checked), disabled while not
    /// <paramref name="connected"/>, and each column's title; a sortable one is a button that sorts by it and shows an arrow while
    /// the table is sorted by it.
    /// </summary>
    public static Element Header<TSort>(ListColumns<TSort> columns, bool compact, bool? checkedState, bool connected, Action<bool> onCheckAll, ListSortView<TSort> sort)
        where TSort : struct, Enum
    {
        var cells = new List<Element>();
        for (var i = 0; i < columns.All.Length; i++)
        {
            var column = columns.All[i];
            if (compact && column.WideOnly)
                continue;

            Element? cell =
                column.IsSelect ? SelectBox(checkedState, onCheckAll, "Select all").IsEnabled(connected)
                : column is { Title: { } title, SortBy: { } by } ? SortTitle(title, by, column.AlignRight, sort)
                : column.Title is { } plain ? ColumnTitle(plain, column.AlignRight)
                : null;
            if (cell is not null)
                cells.Add(cell.Grid(column: i));
        }

        return Row(columns, compact, HeaderHeight, [.. cells]);
    }

    /// <summary>
    /// One row of the table: <paramref name="cells"/> (each placed with <c>.Grid(column: n)</c>) in the table's columns, at least
    /// <paramref name="minHeight"/> tall, with a divider under it. In the compact layout a cell in a wide-only column is left out, as
    /// the header leaves out its title, so a page can pass the same cells in both layouts.
    /// </summary>
    public static Element Row<TSort>(ListColumns<TSort> columns, bool compact, double minHeight, params Element[] cells) where TSort : struct, Enum
    {
        var shown = compact ? Array.FindAll(cells, cell => !columns.IsWideOnly(ColumnOf(cell))) : cells;
        return Border(Grid(columns.Widths(compact), OneRow, shown).MinHeight(minHeight).Padding(12, 0, 8, 0))
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 0, 0, 1);
    }

    /// <summary>
    /// The table: <paramref name="header"/> above the virtualized rows. <paramref name="items"/> is the flat list of what the table
    /// shows, each with a stable <paramref name="key"/>; pass the same list instance while nothing in it changed (a <c>UseMemo</c>),
    /// since the list control is handed it as its items.
    /// </summary>
    public static Element Table<TItem>(Element header, IReadOnlyList<TItem> items, Func<TItem, string> key, Func<TItem, Element> row)
    {
        var list = ListView(items, key, (item, _) => row(item).WithKey(key(item)))
            .Set(view =>
            {
                view.SelectionMode = ListViewSelectionMode.None;
                view.ItemContainerStyle ??= RowContainerStyle();
            });

        return (FlexColumn(
                header.Flex(shrink: 0),
                list.Flex(grow: 1, basis: 0))
            with
            {
                RowGap = 0,
            })
            .Flex(grow: 1, basis: 0);
    }

    /// <summary>
    /// In the table's place while the connection's first list has not arrived (an empty list then does not yet mean there is
    /// nothing): a progress ring that a screen reader names <paramref name="automationName"/> ("Loading images").
    /// </summary>
    public static Element Loading(string automationName) =>
        (FlexColumn(ProgressRing().Size(32, 32).AutomationName(automationName)) with { AlignItems = FlexAlign.Center })
            .Margin(0, 24, 0, 0)
            .Flex(grow: 1, basis: 0);

    /// <summary>
    /// In the table's place when the connection's first read of the list failed: the engine's <paramref name="message"/> as it is, and
    /// Retry (<paramref name="onRetry"/>), which reads the list again.
    /// </summary>
    public static Element LoadFailed(string message, Action onRetry) =>
        (FlexColumn(Widgets.RetryBar(message, onRetry).AutomationId("list-error")) with { RowGap = 0 })
            .Flex(grow: 1, basis: 0);

    /// <summary>In the table's place when it has no rows: why (<paramref name="text"/>: nothing on the engine, or nothing matches).</summary>
    public static Element EmptyState(string text) =>
        Body(text).Foreground(Theme.SecondaryText).Margin(12, 8, 0, 0).Flex(grow: 1, basis: 0);

    /// <summary>A row's or the header's checkbox; <paramref name="label"/> is its accessible name ("Select web-1").</summary>
    public static Element SelectBox(bool? checkedState, Action<bool> onChanged, string label) =>
        CheckBox(checkedState, onChanged)
            .AutomationName(label)
            .MinWidth(0)
            .VAlign(VerticalAlignment.Center);

    /// <summary>A cell's text on one line, trimmed with an ellipsis, the whole text as its tooltip; <paramref name="color"/> overrides the text colour.</summary>
    public static Element CellText(string text, ThemeRef? color = null) =>
        (color is { } drawn ? Widgets.OneLine(Body(text)).Foreground(drawn) : Widgets.OneLine(Body(text)))
            .ToolTip(text)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 12, 0);

    /// <summary>True when every one of <paramref name="keys"/> is checked, false when none is, null when some are.</summary>
    public static bool? CheckedState(IEnumerable<string> keys, IReadOnlySet<string> checkedKeys)
    {
        int all = 0, on = 0;
        foreach (var key in keys)
        {
            all++;
            if (checkedKeys.Contains(key))
                on++;
        }

        return on == 0 ? false : on == all ? true : null;
    }

    private static readonly GridSize[] OneRow = [GridSize.Star()];

    /// <summary>A title that does not sort; a number column's ends where its cells' text ends.</summary>
    private static Element ColumnTitle(string text, bool alignRight)
    {
        var title = Caption(text).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center);
        return alignRight ? title.HAlign(HorizontalAlignment.Right).Margin(0, 0, NumberCellEndMargin, 0) : title;
    }

    /// <summary>The grid column a cell was placed in with <c>.Grid(column: n)</c>; 0 when it was not placed.</summary>
    private static int ColumnOf(Element cell) =>
        cell.Attached is { } attached && attached.TryGetValue(typeof(GridAttached), out var placed) && placed is GridAttached grid ? grid.Column : 0;

    /// <summary>
    /// A column title as a borderless button: its text, and an arrow while the table is sorted by it (up ascending, down
    /// descending). Its accessible name says the state ("Name, sorted ascending"); its AutomationId is <c>sort-</c> and the column
    /// in lower case. The negative margin keeps the text in line with the column's cells while the button keeps its padding for
    /// the hover background.
    /// </summary>
    private static Element SortTitle<TSort>(string title, TSort column, bool alignRight, ListSortView<TSort> sort) where TSort : struct, Enum
    {
        var sorted = EqualityComparer<TSort>.Default.Equals(sort.Column, column);
        // A stack, not a flex row: a flex row takes the whole width it is offered, which would stretch the button over the cell.
        var content = HStack(
            4,
            Caption(title).Foreground(sorted ? Theme.PrimaryText : Theme.SecondaryText).VAlign(VerticalAlignment.Center),
            sorted ? Widgets.Glyph(sort.Descending ? Glyphs.ArrowDown : Glyphs.ArrowUp, size: 12, color: Theme.PrimaryText).VAlign(VerticalAlignment.Center) : Empty());

        var button = Button(content, () => sort.OnSort(column))
            .SubtleButton()
            .AutomationId("sort-" + column.ToString().ToLowerInvariant())
            .AutomationName(sort.AccessibleName(column))
            .Padding(8, 4, 8, 4);

        // A number column is right-aligned: the title's text ends where the cells' text ends (the button's padding is 8 of it).
        return alignRight
            ? button.Margin(0, 0, NumberCellEndMargin - 8, 0).HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Center)
            : button.Margin(-8, 0, 0, 0).HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Center);
    }

    /// <summary>The list item container stretches its row over the full width and adds no padding, so rows line up with the header.</summary>
    private static Style RowContainerStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        return style;
    }
}

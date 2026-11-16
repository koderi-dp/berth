using System.Collections.Immutable;
using Berth.Components;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Images;

/// <summary>
/// What the images table calls back. One instance per page, kept across renders, so the table's props stay equal while nothing it
/// shows changed; the page points its members at its latest handlers on every render.
/// </summary>
internal sealed class ImageTableHandlers
{
    /// <summary>Checks (true) or unchecks the rows with these keys.</summary>
    public Action<IEnumerable<string>, bool> SetChecked { get; set; } = (_, _) => { };

    /// <summary>A click on a column title.</summary>
    public Action<ImageColumn> SortBy { get; set; } = _ => { };

    /// <summary>A row's action, from its buttons, its "more" menu or its right-click menu.</summary>
    public Action<ImageRow, RowAction> Act { get; set; } = (_, _) => { };
}

/// <summary>
/// What the images table shows: the rows the filter and search keep (a memoized list), the checked keys, the layout, whether the
/// engine is connected, the sort, and the running pulls' rows (which render themselves). A record: while a pull's progress changes the
/// page ten times a second, this is equal and the table is not built again.
/// </summary>
internal sealed record ImageTableProps(
    IReadOnlyList<ImageRow> Rows,
    ImmutableHashSet<string> Checked,
    bool Compact,
    bool Connected,
    ListSort<ImageColumn> Sort,
    PullRowsProps Pulls,
    ImageTableHandlers Handlers);

/// <summary>
/// The images table: the header (select all, sortable Name, Tag, Created and Size), the pulls under way, then a row per tag or untagged
/// image with its checkbox, in-use mark, name, tag, image ID with copy (wide windows only), created, size and actions.
/// </summary>
internal sealed class ImageTable : Component<ImageTableProps>
{
    /// <summary>The window is at least this wide (in effective pixels) when the image ID has its own column.</summary>
    public const double WideLayoutMinWidth = 1300;

    private const double RowHeight = 48;

    /// <summary>
    /// Checkbox, in-use mark, name, tag, image ID (wide only), created, size, actions (Run, the menu, a divider and Delete). The pull rows
    /// lay out in <see cref="PullRow"/>'s own columns, which start where these do.
    /// </summary>
    public static readonly ListColumns<ImageColumn> Columns = new(
        ListColumn<ImageColumn>.Select(),
        ListColumn<ImageColumn>.Of(GridSize.Px(28)),
        ListColumn<ImageColumn>.Of(GridSize.Star(3)) with { Title = "Name", SortBy = ImageColumn.Name },
        ListColumn<ImageColumn>.Of(GridSize.Star(2)) with { Title = "Tag", SortBy = ImageColumn.Tag },
        ListColumn<ImageColumn>.InWideOnly(GridSize.Px(168)) with { Title = "Image ID" },
        ListColumn<ImageColumn>.Of(GridSize.Px(120)) with { Title = "Created", SortBy = ImageColumn.Created },
        ListColumn<ImageColumn>.Of(GridSize.Px(112)) with { Title = "Size", SortBy = ImageColumn.Size, AlignRight = true },
        ListColumn<ImageColumn>.Of(GridSize.Px(156)));

    public override Element Render()
    {
        var (rows, checkedKeys, compact, connected, sort, pulls, handlers) = Props;
        var shownKeys = UseMemo(() => rows.Select(r => r.Key).ToArray(), rows);

        var header = FlexColumn(
            ListTable.Header(
                Columns,
                compact,
                ListTable.CheckedState(shownKeys, checkedKeys),
                connected,
                value => handlers.SetChecked(shownKeys, value),
                ListSortView.Of(sort, column => handlers.SortBy(column))),
            Component<PullRows, PullRowsProps>(pulls));

        var now = DateTimeOffset.UtcNow;
        return ListTable.Table(header, rows, r => r.Key, r => Row(r, checkedKeys.Contains(r.Key), connected, compact, now, handlers));
    }

    /// <summary>One tag of an image, or an untagged image.</summary>
    private static Element Row(ImageRow row, bool isChecked, bool connected, bool compact, DateTimeOffset now, ImageTableHandlers handlers)
    {
        var actions = RowActions.ForImage(row, connected);
        void Run(RowAction action) => handlers.Act(row, action);
        var label = row.IsTagged ? row.Key : row.DisplayName;

        var id = (FlexRow(
                Widgets.OneLine(Body(row.ShortId)).Foreground(Theme.SecondaryText),
                Widgets.IconButton(Glyphs.Copy, "Copy ID", () => ContainerParts.CopyToClipboard(row.ImageId), size: 12)
                    .AutomationId("copy-id-" + row.Key)
                    .AutomationName("Copy the ID of " + label)
                    .Padding(4)
                    .MinWidth(24)
                    .MinHeight(24))
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 2,
            })
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 12, 0);

        return ListTable.Row(
                Columns,
                compact,
                RowHeight,
                ListTable.SelectBox(isChecked, value => handlers.SetChecked((string[])[row.Key], value), "Select " + label).IsEnabled(connected).Grid(column: 0),
                InUseMark(row).Grid(column: 1),
                ListTable.CellText(row.Name, row.IsTagged ? null : Theme.SecondaryText).Grid(column: 2),
                ListTable.CellText(row.Tag, row.IsTagged ? null : Theme.SecondaryText).Grid(column: 3),
                id.Grid(column: 4), // left out in the compact layout; the ID is still in the menus and in Inspect
                ListTable.CellText(AgeFormat.Ago(now, row.Created)).Grid(column: 5),
                Body(ByteFormat.Bytes(row.Size))
                    .TextWrapping(TextWrapping.NoWrap)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)
                    .Margin(0, 0, ListTable.NumberCellEndMargin, 0)
                    .Grid(column: 6),
                ContainerParts.ActionButtons(actions, compact, row.Key, Run).Grid(column: 7))
            .WithContextFlyout(ContainerParts.ContextMenu(actions, Run));
    }

    /// <summary>
    /// A ring while a remove of the image runs; a filled dot in the success colour when a container uses it (its tooltip names them);
    /// nothing when none does.
    /// </summary>
    private static Element InUseMark(ImageRow row)
    {
        if (row.IsPending)
            return ContainerParts.StateMark(StateShape.Filled, Theme.SecondaryText, "Removing", busy: true);
        if (!row.InUse)
            return Empty();

        var users = row.UsedBy.Count > 0
            ? "In use by " + string.Join(", ", row.UsedBy)
            : row.UsedByCount == 1 ? "In use by 1 container" : $"In use by {row.UsedByCount} containers";
        return ContainerParts.StateMark(StateShape.Filled, Theme.SystemSuccess, users, busy: false);
    }
}

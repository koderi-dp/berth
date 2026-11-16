using System.Collections.Immutable;
using Berth.Components;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Volumes;

/// <summary>What the volumes table calls back (see <see cref="TableHandlers{TRow, TSort}"/>); View details opens the volume's page.</summary>
internal sealed class VolumeTableHandlers : TableHandlers<VolumeRow, VolumeColumn>;

/// <summary>
/// What the volumes table shows: the rows the filter and search keep (a memoized list), the checked keys, whether the engine is connected,
/// whether the disk use has been read (sizes are known), the sort and the page's handlers. A record, so the table is built again only
/// when one of them changes.
/// </summary>
internal sealed record VolumeTableProps(
    IReadOnlyList<VolumeRow> Rows,
    ImmutableHashSet<string> Checked,
    bool Connected,
    bool SizesRead,
    ListSort<VolumeColumn> Sort,
    VolumeTableHandlers Handlers);

/// <summary>
/// The volumes table: the header (select all, sortable Name, Created and Size), then a row per volume with its checkbox, in-use mark,
/// name (a link to its page), created, size and actions (the "more" menu with View details and Copy name, a divider and Delete; the same
/// actions in the right-click menu).
/// </summary>
internal sealed class VolumeTable : Component<VolumeTableProps>
{
    private const double RowHeight = 48;

    /// <summary>Checkbox, in-use mark, name, created, size, actions. No column needs the wide layout, so the table has one form.</summary>
    private static readonly ListColumns<VolumeColumn> Columns = new(
        ListColumn<VolumeColumn>.Select(),
        ListColumn<VolumeColumn>.Of(GridSize.Px(28)),
        ListColumn<VolumeColumn>.Of(GridSize.Star()) with { Title = "Name", SortBy = VolumeColumn.Name },
        ListColumn<VolumeColumn>.Of(GridSize.Px(140)) with { Title = "Created", SortBy = VolumeColumn.Created },
        ListColumn<VolumeColumn>.Of(GridSize.Px(112)) with { Title = "Size", SortBy = VolumeColumn.Size, AlignRight = true },
        ListColumn<VolumeColumn>.Of(GridSize.Px(96)));

    public override Element Render()
    {
        var (rows, checkedKeys, connected, sizesRead, sort, handlers) = Props;
        var shownKeys = UseMemo(() => rows.Select(r => r.Key).ToArray(), rows);

        var header = ListTable.Header(
            Columns,
            compact: false,
            ListTable.CheckedState(shownKeys, checkedKeys),
            connected,
            value => handlers.SetChecked(shownKeys, value),
            ListSortView.Of(sort, column => handlers.SortBy(column)));

        var now = DateTimeOffset.UtcNow;
        return ListTable.Table(header, rows, r => r.Key, r => Row(r, checkedKeys.Contains(r.Key), connected, sizesRead, now, handlers));
    }

    private static Element Row(VolumeRow row, bool isChecked, bool connected, bool sizesRead, DateTimeOffset now, VolumeTableHandlers handlers)
    {
        var actions = RowActions.ForVolume(row, connected);
        void Run(RowAction action) => handlers.Act(row, action);
        var viewDetails = actions.Sections[0][0];

        // The name opens the volume's page; the button is as wide as the name, so a click beside it does not open it.
        var name = HyperlinkButton(Widgets.OneLine(Body(row.ShownName)), onClick: () => Run(viewDetails))
            .AutomationId("open-" + row.Name)
            .AutomationName(row.Name)
            .ToolTip(row.Name)
            .Padding(0)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 12, 0);

        var size = sizesRead
            ? Body(ByteFormat.Bytes(row.Size))
            : Body("…").ToolTip("Reading the disk use");

        return ListTable.Row(
                Columns,
                compact: false,
                RowHeight,
                ListTable.SelectBox(isChecked, value => handlers.SetChecked((string[])[row.Key], value), "Select " + row.Name).IsEnabled(connected).Grid(column: 0),
                InUseMark(row).Grid(column: 1),
                name.Grid(column: 2),
                ListTable.CellText(AgeFormat.Ago(now, row.Created)).Grid(column: 3),
                size
                    .TextWrapping(TextWrapping.NoWrap)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)
                    .Margin(0, 0, ListTable.NumberCellEndMargin, 0)
                    .Grid(column: 4),
                ContainerParts.ActionButtons(actions, compact: false, row.Name, Run).Grid(column: 5))
            .WithContextFlyout(ContainerParts.ContextMenu(actions, Run));
    }

    /// <summary>A ring while the volume is removed or created; a filled dot when a container mounts it (its tooltip names them); nothing otherwise.</summary>
    private static Element InUseMark(VolumeRow row)
    {
        if (row.IsPending)
            return ContainerParts.StateMark(StateShape.Filled, Theme.SecondaryText, "Working", busy: true);
        if (!row.InUse)
            return Empty();
        return ContainerParts.StateMark(StateShape.Filled, Theme.SystemSuccess, "In use by " + string.Join(", ", row.UsedBy), busy: false);
    }
}

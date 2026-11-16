using System.Collections.Immutable;
using Berth.Components;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using MenuEntry = Microsoft.UI.Reactor.Core.MenuFlyoutItemBase;

namespace Berth.Pages.Networks;

/// <summary>What the networks table calls back (see <see cref="TableHandlers{TRow, TSort}"/>), and opening a container's page.</summary>
internal sealed class NetworkTableHandlers : TableHandlers<NetworkRow, NetworkColumn>
{
    /// <summary>A container named in a network's containers menu (its ID).</summary>
    public Action<string> OpenContainer { get; set; } = _ => { };
}

/// <summary>
/// What the networks table shows: the rows the filter and search keep and each one's attached containers (memoized), the checked keys,
/// the layout, whether the engine is connected, the sort and the page's handlers. A record, so the table is built again only when one of
/// them changes.
/// </summary>
internal sealed record NetworkTableProps(
    IReadOnlyList<NetworkRow> Rows,
    IReadOnlyDictionary<string, IReadOnlyList<NetworkMember>> Members,
    ImmutableHashSet<string> Checked,
    bool Compact,
    bool Connected,
    ListSort<NetworkColumn> Sort,
    NetworkTableHandlers Handlers);

/// <summary>
/// The networks table: the header (select all, sortable Name, Driver, Scope, Containers and Created), then a row per network with its
/// checkbox, name, driver, scope (wide windows only), how many containers are attached (their names in the tooltip, a menu of links to
/// their pages), created and actions (Copy name and Copy ID in the "more" menu, a divider and Delete). A built-in network (bridge, host,
/// none) has no Delete and its checkbox is disabled, with a tooltip saying why.
/// </summary>
internal sealed class NetworkTable : Component<NetworkTableProps>
{
    /// <summary>The window is at least this wide (in effective pixels) when the scope has its own column.</summary>
    public const double WideLayoutMinWidth = 1300;

    private const double RowHeight = 48;

    private const string BuiltInNote = "Built-in network; it cannot be removed.";

    private static readonly ListColumns<NetworkColumn> Columns = new(
        ListColumn<NetworkColumn>.Select(),
        ListColumn<NetworkColumn>.Of(GridSize.Star(3)) with { Title = "Name", SortBy = NetworkColumn.Name },
        ListColumn<NetworkColumn>.Of(GridSize.Px(120)) with { Title = "Driver", SortBy = NetworkColumn.Driver },
        ListColumn<NetworkColumn>.InWideOnly(GridSize.Px(104)) with { Title = "Scope", SortBy = NetworkColumn.Scope },
        ListColumn<NetworkColumn>.Of(GridSize.Px(128)) with { Title = "Containers", SortBy = NetworkColumn.Containers },
        ListColumn<NetworkColumn>.Of(GridSize.Px(140)) with { Title = "Created", SortBy = NetworkColumn.Created },
        ListColumn<NetworkColumn>.Of(GridSize.Px(96)));

    public override Element Render()
    {
        var (rows, members, checkedKeys, compact, connected, sort, handlers) = Props;
        // Select all checks only what can be removed: a built-in network is never checked.
        var selectableKeys = UseMemo(() => rows.Where(r => !r.IsBuiltIn).Select(r => r.Key).ToArray(), rows);

        var header = ListTable.Header(
            Columns,
            compact,
            ListTable.CheckedState(selectableKeys, checkedKeys),
            connected && selectableKeys.Length > 0,
            value => handlers.SetChecked(selectableKeys, value),
            ListSortView.Of(sort, column => handlers.SortBy(column)));

        var now = DateTimeOffset.UtcNow;
        return ListTable.Table(
            header,
            rows,
            r => r.Key,
            r => Row(r, members.GetValueOrDefault(r.Key) ?? [], checkedKeys.Contains(r.Key), connected, compact, now, handlers));
    }

    private static Element Row(
        NetworkRow row, IReadOnlyList<NetworkMember> members, bool isChecked, bool connected, bool compact, DateTimeOffset now, NetworkTableHandlers handlers)
    {
        var actions = RowActions.ForNetwork(row, connected);
        void Run(RowAction action) => handlers.Act(row, action);

        var select = row.IsBuiltIn
            ? ListTable.SelectBox(false, _ => { }, "Select " + row.Name).IsEnabled(false).ToolTip(BuiltInNote)
            : ListTable.SelectBox(isChecked, value => handlers.SetChecked((string[])[row.Key], value), "Select " + row.Name).IsEnabled(connected);

        var name = row.IsPending
            ? HStack(8, ProgressRing().Size(16, 16).AutomationName("Removing " + row.Name), Widgets.OneLine(Body(row.Name))).VAlign(VerticalAlignment.Center).ToolTip(row.Name)
            : ListTable.CellText(row.Name, row.IsBuiltIn ? Theme.SecondaryText : null);

        return ListTable.Row(
                Columns,
                compact,
                RowHeight,
                select.Grid(column: 0),
                name.Margin(0, 0, 12, 0).Grid(column: 1),
                ListTable.CellText(row.Driver).Grid(column: 2),
                ListTable.CellText(row.Scope).Grid(column: 3),
                ContainersCell(row, members, handlers).Grid(column: 4),
                ListTable.CellText(AgeFormat.Ago(now, row.Created)).Grid(column: 5),
                ContainerParts.ActionButtons(actions, compact, row.Name, Run).Grid(column: 6))
            .WithContextFlyout(ContainerParts.ContextMenu(actions, Run));
    }

    /// <summary>
    /// "0", or how many containers are attached as a button whose tooltip names them and whose menu lists them, each opening its page
    /// (AutomationId <c>containers-</c> and the network's name).
    /// </summary>
    private static Element ContainersCell(NetworkRow row, IReadOnlyList<NetworkMember> members, NetworkTableHandlers handlers)
    {
        if (members.Count == 0)
            return Body("0").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).AutomationId("containers-" + row.Name);

        var names = string.Join(", ", members.Select(m => m.ContainerName));
        MenuEntry[] items =
        [
            .. members.Select(m => (MenuEntry)(MenuItem(m.ContainerName, () => handlers.OpenContainer(m.ContainerId)) with
            {
                IconElement = Widgets.MenuGlyph(Glyphs.Details),
            })),
        ];
        return Button(Body(members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), null)
            .SubtleButton()
            .WithFlyout(MenuItems(items))
            .AutomationId("containers-" + row.Name)
            .AutomationName($"{members.Count} attached: {names}")
            .ToolTip(names)
            .Padding(8, 4, 8, 4)
            .Margin(-8, 0, 0, 0)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Center);
    }
}

using System.Collections.Immutable;
using Berth.Components;
using Berth.Hooks;
using Berth.Pages.Networks;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Navigation;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>
/// The networks on the engine: search and a filter (all, in use, unused), a row per network with its driver, scope, attached containers
/// (a menu of links to them), age, a "more" menu (Copy name, Copy ID) and Delete (none for the built-in networks); Remove unused networks
/// and a bulk Delete, which confirm what they remove (naming the containers that lose a network) and show the batch's progress and result
/// above the list. <c>App</c> sends <see cref="NetworksWanted"/> each time this page is shown.
/// </summary>
internal sealed class NetworksPage : ListPageComponent
{
    /// <summary>The filter's choices, in <see cref="UsageShow"/>'s order.</summary>
    private static readonly string[] ShowChoices = ["All networks", "In use", "Unused"];

    private static readonly object NoBatch = new();

    public override Element Render()
    {
        var state = UseEngineState();
        var compact = !UseBreakpoint(NetworkTable.WideLayoutMinWidth);
        var compactToolbar = !UseBreakpoint(ListPage.ToolbarWideMinWidth);
        var (view, updateView) = UseListView(AppRoute.Networks, new ResourceListView<UsageFilter, NetworkColumn>(UsageFilter.All, NetworkRows.DefaultSort));
        var (filter, sort) = (view.Filter, view.Sort);
        var searchBox = UseListSearch();
        var nav = UseNavigation<AppRoute>();
        var handlers = UseMemo(() => new NetworkTableHandlers(), Array.Empty<object>());

        var connected = state.Status == ConnectionStatus.Connected;
        var all = UseMemo(() => NetworkRows.From(state), state.Networks, state.Containers);
        var rows = UseMemo(() => NetworkRows.Shown(all, filter, sort), all, filter, sort);
        var members = UseMemo(
            () => (IReadOnlyDictionary<string, IReadOnlyList<NetworkMember>>)rows.ToDictionary(r => r.Key, r => NetworkRows.Members(state, r.Name), StringComparer.Ordinal),
            rows,
            state.Containers);
        var (checkedKeys, setChecked) = UseCheckedKeys(all);

        var deleteKeys = NetworkRows.RemoveKeys(rows, checkedKeys, connected);
        var removal = state.Networks.Removal;
        var removalPanel = UseMemo(() => RemovalPanel.Render(removal, ResourceKind.Networks, NameOf(state)), removal ?? NoBatch, state.Networks.Items);
        // A delete plans from the rows shown (the filter and search), when asked and again at confirm; never a built-in network.
        var remove = UseRemoveDialogs(state, (now, keys) =>
            Confirmations.RemoveNetworks(NetworkRows.Shown(NetworkRows.From(now), view.Filter, view.Sort), keys, now.Status == ConnectionStatus.Connected));
        var unusedNotKnown = Confirmations.UnusedNotKnown(state);

        handlers.SetChecked = setChecked;
        handlers.SortBy = column => updateView(v => v with { Sort = v.Sort.Toggle(column) });
        handlers.OpenContainer = id => nav.Navigate(new AppRoute.ContainerDetail(id, AppServices.Current.SessionNumber), new NavigateOptions { Transition = NavigationTransition.DrillIn() });
        handlers.Act = (row, action) =>
        {
            switch (action.Kind)
            {
                case RowActionKind.CopyName:
                    ContainerParts.CopyToClipboard(row.Name);
                    break;
                case RowActionKind.CopyId:
                    ContainerParts.CopyToClipboard(row.Id);
                    break;
                case RowActionKind.Delete:
                    remove.AskDelete((string[])[row.Key]);
                    break;
            }
        };

        var keys = ListPage.Keys(searchBox, dialogOpen: remove.Dialog is not null, connected, deleteKeys.Length > 0 ? () => remove.AskDelete(deleteKeys) : null);
        UseEffect(() => ShellKeys.Register(AppRoute.Networks, keys), keys);

        var toolbar = ListPage.Toolbar(
            ListPage.SearchBox(filter.Search, value => updateView(v => v with { Filter = v.Filter with { Search = value } }), searchBox, "Search by name, driver or ID (Ctrl+F)"),
            ComboBox(ShowChoices, (int)filter.Show, index => updateView(v => v with { Filter = v.Filter with { Show = (UsageShow)Math.Max(0, index) } }))
                .AutomationId("network-filter")
                .AutomationName("Show")
                .MinWidth(160),
            compactToolbar,
            primary: null,
            new ToolbarAction(
                "Remove unused networks…",
                "remove-unused-networks",
                () => remove.AskUnused(Confirmations.PruneNetworks(state)),
                connected && removal is not { IsRunning: true } && unusedNotKnown is null,
                unusedNotKnown ?? "Remove every network, other than the built-in ones, that no container is attached to"));

        var bulk = ListPage.BulkBar(
            RowSelection.SelectedCount(rows, checkedKeys),
            Button("Delete", () => remove.AskDelete(deleteKeys))
                .AutomationId("delete-selected")
                .ToolTip("Delete the selected networks (Delete)")
                .IsEnabled(deleteKeys.Length > 0));

        return ListPage.Render(
            "Networks",
            state,
            summary: null,
            toolbar,
            ListPage.Bars(removalPanel, bulk),
            Table(state, rows, members, filter, checkedKeys, compact, connected, sort, handlers),
            remove.Dialog ?? Empty());
    }

    /// <summary>A line about a remove names the network by its name while it is listed, not by its ID.</summary>
    private static Func<string, string> NameOf(EngineState state) =>
        key => state.Networks.Items.FirstOrDefault(n => n.Id == key)?.Name ?? key;

    private static Element Table(
        EngineState state,
        IReadOnlyList<NetworkRow> rows,
        IReadOnlyDictionary<string, IReadOnlyList<NetworkMember>> members,
        UsageFilter filter,
        ImmutableHashSet<string> checkedKeys,
        bool compact,
        bool connected,
        ListSort<NetworkColumn> sort,
        NetworkTableHandlers handlers)
    {
        var networks = state.Networks;
        return ListTable.States(networks, rows.Count, "Loading networks", new NetworksWanted(), NetworkRows.EmptyText(!networks.Items.IsEmpty, filter))
            ?? Component<NetworkTable, NetworkTableProps>(new(rows, members, checkedKeys, compact, connected, sort, handlers)).Flex(grow: 1, basis: 0);
    }
}

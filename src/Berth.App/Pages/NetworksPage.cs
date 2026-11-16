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
    /// <summary>The page's one dialog at a time.</summary>
    private abstract record Dialog;

    /// <summary>A delete: the keys the user named and the plan's text when the dialog opened.</summary>
    private sealed record DeleteDialogOpen(ImmutableArray<string> Keys, RemovalPlan Plan) : Dialog;

    private sealed record PruneDialogOpen(PrunePlan Plan) : Dialog;

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
        var (dialog, setDialog) = UseState<Dialog?>(null);
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

        void AskDelete(IEnumerable<string> keys)
        {
            var named = keys.ToImmutableArray();
            var plan = Confirmations.RemoveNetworks(rows, named, connected);
            if (!plan.IsEmpty)
                setDialog(new DeleteDialogOpen(named, plan));
        }

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
                    AskDelete((string[])[row.Key]);
                    break;
            }
        };

        var keys = ListPage.Keys(searchBox, dialogOpen: dialog is not null, connected, deleteKeys.Length > 0 ? () => AskDelete(deleteKeys) : null);
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
                () => setDialog(new PruneDialogOpen(Confirmations.PruneNetworks(state))),
                connected && removal is not { IsRunning: true },
                "Remove every network, other than the built-in ones, that no container is attached to"));

        var bulk = ListPage.BulkBar(
            RowSelection.SelectedCount(rows, checkedKeys),
            Button("Delete", () => AskDelete(deleteKeys))
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
            DialogOf(dialog, () => setDialog(null), view));
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
        if (rows.Count == 0 && !networks.Loaded)
        {
            // The connection's first list has not arrived (or its read failed): an empty list here does not mean there are no networks.
            return networks.LoadError is { } error
                ? ListTable.LoadFailed(error, () => AppServices.Current.Session.Dispatch(new NetworksWanted()))
                : ListTable.Loading("Loading networks");
        }

        if (rows.Count == 0)
            return ListTable.EmptyState(NetworkRows.EmptyText(!networks.Items.IsEmpty, filter));

        return Component<NetworkTable, NetworkTableProps>(new(rows, members, checkedKeys, compact, connected, sort, handlers)).Flex(grow: 1, basis: 0);
    }

    /// <summary>The dialog open, or an empty element in its place (the page's dialog slot is always there).</summary>
    private static Element DialogOf(Dialog? dialog, Action close, ResourceListView<UsageFilter, NetworkColumn> view) => dialog switch
    {
        DeleteDialogOpen delete => RemoveDialog.Render(delete.Plan.Title, delete.Plan.Text, canConfirm: true, () => ConfirmDelete(delete.Keys, view), close),
        PruneDialogOpen prune => RemoveDialog.Render(prune.Plan.Title, prune.Plan.Text, !prune.Plan.IsEmpty, () => RemoveDialog.SendUnused(prune.Plan), close),
        _ => Empty(),
    };

    /// <summary>Sends the confirmed delete as it stands now: of the keys named, the rows the filter and search show, listed, not pending and not built in.</summary>
    private static void ConfirmDelete(ImmutableArray<string> keys, ResourceListView<UsageFilter, NetworkColumn> view)
    {
        var state = AppServices.Current.Session.State;
        RemoveDialog.Send(Confirmations.RemoveNetworks(NetworkRows.Shown(NetworkRows.From(state), view.Filter, view.Sort), keys, state.Status == ConnectionStatus.Connected));
    }
}

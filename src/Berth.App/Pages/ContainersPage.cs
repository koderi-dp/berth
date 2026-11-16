using System.Collections.Immutable;
using Berth.Components;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Berth.Settings;
using Berth.State;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>
/// The containers of the engine, grouped by compose project, with search, a "running only" switch, actions per row, per project and
/// for the checked rows, and a confirmation before anything is deleted.
/// </summary>
internal sealed class ContainersPage : ListPageComponent
{
    /// <summary>
    /// What the list shows apart from its rows and is kept if the page is built again: the search and the collapsed compose projects.
    /// The "running only" switch and the sort are in the settings.
    /// </summary>
    private sealed record View(string Search, ImmutableHashSet<string> Collapsed);

    /// <summary>One line of the virtualized list: a compose project's row or a container's row.</summary>
    private abstract record ListItem(string Key);

    private sealed record GroupItem(ContainerGroup Group, bool Collapsed) : ListItem("group:" + Group.Project);

    private sealed record RowItem(ContainerRow Row, string? Project) : ListItem(Row.Id);

    private static readonly ImmutableHashSet<string> NoIds = ImmutableHashSet<string>.Empty;

    /// <summary>The sort's stats dependency while it does not sort by CPU.</summary>
    private static readonly object NoStats = new();

    public override Element Render()
    {
        var state = UseEngineState();
        // A narrow window has no room for a button per action: Restart and Pause then go into a menu on each row.
        var compact = !UseBreakpoint(ContainerRowView.WideLayoutMinWidth);
        var settings = UseExternalStore(SettingsHook.Subscribe, SettingsHook.Snapshot);
        var (view, updateView) = UseListView(AppRoute.Containers, new View("", NoIds));
        var (search, collapsed) = (view.Search, view.Collapsed);
        var (checkedIds, updateChecked) = UseReducer(NoIds);
        var (deleteRequest, setDeleteRequest) = UseState<DeleteRequest?>(null);
        // A switch of connection: the checked containers and an open delete belonged to the old engine.
        UseSessionReset(() =>
        {
            updateChecked(_ => NoIds);
            setDeleteRequest(null);
        });
        var searchBox = UseListSearch();
        var nav = UseNavigation<AppRoute>();

        var connected = state.Status == ConnectionStatus.Connected;
        var runningOnly = settings.ContainerList.RunningOnly;
        // The saved sort, except that a CPU sort shows as by name while the narrow layout has no CPU column.
        var sort = settings.ContainerList.Sort.Shown(cpuColumnShown: !compact);
        var filter = UseMemo(() => new ContainerFilter(search, runningOnly), search, runningOnly);
        // Sorted by CPU, the order follows each batch of stats; otherwise the stats sort nothing and are no dependency.
        var sortStats = sort.Column == SortColumn.Cpu ? state.Stats : null;
        var groups = UseMemo(() => sort.Apply(ComposeGrouping.Build(state.Containers, state.Pending, filter), sortStats), state.Containers, state.Pending, filter, sort, sortStats ?? NoStats);
        var items = UseMemo(() => Flatten(groups, collapsed), groups, collapsed);
        // The rows the filter shows, collapsed projects included: only these count for the bulk actions.
        var visibleRows = UseMemo(() => groups.SelectMany(g => g.Rows).ToArray(), groups);

        // A container that is gone cannot stay checked.
        UseEffect(() =>
        {
            var existing = state.Containers.Select(c => c.Id).ToHashSet();
            updateChecked(ids => ids.All(existing.Contains) ? ids : ids.Intersect(existing));
        }, state.Containers);

        var callbacks = new ContainerListCallbacks(
            Send,
            (ids, value) => updateChecked(current => value ? current.Union(ids) : current.Except(ids)),
            project => updateView(v => v with { Collapsed = v.Collapsed.Contains(project) ? v.Collapsed.Remove(project) : v.Collapsed.Add(project) }),
            rows => setDeleteRequest(DeleteRequest.For(rows)),
            row => nav.Navigate(new AppRoute.ContainerDetail(row.Id, AppServices.Current.SessionNumber), new NavigateOptions { Transition = NavigationTransition.DrillIn() }));

        var shownIds = visibleRows.Select(r => r.Id).ToArray();
        var deleteRows = DeleteRows(visibleRows, checkedIds, connected);

        // None of the shortcuts acts while the delete confirmation is open.
        var keys = ListPage.Keys(
            searchBox,
            dialogOpen: deleteRequest is not null,
            connected,
            deleteRows.Length > 0 ? () => callbacks.RequestDelete(deleteRows) : null);
        UseEffect(() => ShellKeys.Register(AppRoute.Containers, keys), keys);

        return ListPage.Render(
            "Containers",
            state,
            Strip(state),
            Toolbar(search, value => updateView(v => v with { Search = value }), runningOnly, searchBox),
            BulkBar(visibleRows, checkedIds, deleteRows, connected, callbacks),
            Table(state, filter, sort, groups, items, shownIds, checkedIds, connected, compact, callbacks),
            DeleteDialog.Render(deleteRequest, () => setDeleteRequest(null)));
    }

    /// <summary>The running containers' CPU and memory in total, while connected; drawn again only when a figure changes.</summary>
    private static Element? Strip(EngineState state)
    {
        if (state.Status != ConnectionStatus.Connected)
            return null;
        var totals = StatsSummary.Totals(state);
        return Component<StatsStrip, StatsStripProps>(new(StatsSummary.CpuText(totals), StatsSummary.MemoryText(totals)));
    }

    /// <summary>The checked rows the Delete button and the Delete key ask to remove: shown, not pending, and none while not connected.</summary>
    private static ContainerRow[] DeleteRows(ContainerRow[] visibleRows, ImmutableHashSet<string> checkedIds, bool connected)
    {
        var ids = BulkSelection.DeleteIds(visibleRows, checkedIds, connected);
        return [.. visibleRows.Where(r => ids.Contains(r.Id))];
    }

    /// <summary>Sends <paramref name="action"/> for <paramref name="ids"/> to the session current at the time of the click.</summary>
    private static void Send(IReadOnlyList<string> ids, ContainerAction action)
    {
        if (ids.Count > 0)
            AppServices.Current.Session.Dispatch(new ActionRequested([.. ids], action));
    }

    private static void SetRunningOnly(bool value) =>
        AppServices.Current.UpdateSettings(settings => settings with { ContainerList = settings.ContainerList with { RunningOnly = value } });

    /// <summary>
    /// A click on a column title: sorts by that column, or reverses it when it is the one sorted by (as <paramref name="shown"/>, the
    /// sort the table shows); kept in the settings.
    /// </summary>
    private static void SortBy(ContainerSort shown, SortColumn column) =>
        AppServices.Current.UpdateSettings(settings => settings with
        {
            ContainerList = settings.ContainerList with { Sort = shown.Toggle(column) },
        });

    /// <summary>Groups and rows as one list: a compose project's row, then its rows unless it is collapsed; a container on its own as a plain row.</summary>
    private static IReadOnlyList<ListItem> Flatten(IReadOnlyList<ContainerGroup> groups, ImmutableHashSet<string> collapsed)
    {
        var items = new List<ListItem>();
        foreach (var group in groups)
        {
            if (group.Project is not { } project)
            {
                items.Add(new RowItem(group.Rows[0], Project: null));
                continue;
            }

            var isCollapsed = collapsed.Contains(project);
            items.Add(new GroupItem(group, isCollapsed));
            if (!isCollapsed)
                items.AddRange(group.Rows.Select(row => new RowItem(row, project)));
        }

        return items;
    }

    /// <summary>
    /// The search box and the "running only" switch. The box is uncontrolled (<see cref="Widgets.SearchBox"/>): this page stays in the
    /// navigation cache while a container's page or Settings shows, and a box that is mounted starts from <paramref name="search"/>,
    /// so the box and the filter cannot disagree.
    /// </summary>
    private static Element Toolbar(string search, Action<string> setSearch, bool runningOnly, ListSearch searchBox) =>
        ListPage.Toolbar(
            ListPage.SearchBox(search, setSearch, searchBox, "Search by name, image or ID (Ctrl+F)"),
            ToggleSwitch(runningOnly, SetRunningOnly, onContent: "Only show running containers", offContent: "Only show running containers")
                .AutomationName("Only show running containers"));

    /// <summary>
    /// Shown while shown rows are checked: how many, and Start, Stop and Delete for them (rows with an action in progress are left
    /// out; a button that would send nothing, which is every button while not connected, is disabled). Checked rows the filter
    /// hides are kept but not counted or acted on.
    /// </summary>
    private static Element? BulkBar(ContainerRow[] visibleRows, ImmutableHashSet<string> checkedIds, ContainerRow[] deleteRows, bool connected, ContainerListCallbacks callbacks)
    {
        var count = BulkSelection.SelectedCount(visibleRows, checkedIds);
        if (count == 0)
            return null;

        var start = BulkSelection.StartIds(visibleRows, checkedIds, connected);
        var stop = BulkSelection.StopIds(visibleRows, checkedIds, connected);

        return ListPage.BulkBar(
            count,
            Button("Start", () => callbacks.Send(start, ContainerAction.Start)).IsEnabled(!start.IsEmpty),
            Button("Stop", () => callbacks.Send(stop, ContainerAction.Stop)).IsEnabled(!stop.IsEmpty),
            Button("Delete", () => callbacks.RequestDelete(deleteRows)).ToolTip("Delete the selected containers (Delete)").IsEnabled(deleteRows.Length > 0));
    }

    private static Element Table(
        EngineState state,
        ContainerFilter filter,
        ContainerSort sort,
        IReadOnlyList<ContainerGroup> groups,
        IReadOnlyList<ListItem> items,
        string[] shownIds,
        ImmutableHashSet<string> checkedIds,
        bool connected,
        bool compact,
        ContainerListCallbacks callbacks)
    {
        if (groups.Count == 0 && !state.ListLoaded)
        {
            // The connection's first list has not arrived: an empty list here does not yet mean there are no containers. When its read
            // failed, the engine's message and Retry take the ring's place.
            return state.ContainersLoadError is { } error
                ? ListTable.LoadFailed(error, () => AppServices.Current.Session.Dispatch(new ReloadRequested()))
                : ListTable.Loading("Loading containers");
        }

        if (groups.Count == 0)
            return ListTable.EmptyState(BulkSelection.EmptyText(engineHasContainers: !state.Containers.IsEmpty, filter));

        var header = ContainerRowView.Header(ListTable.CheckedState(shownIds, checkedIds), connected, compact, value => callbacks.SetChecked(shownIds, value), sort, column => SortBy(sort, column));

        return ListTable.Table(header, items, item => item.Key, item => item switch
        {
            GroupItem g => ContainerRowView.Group(g.Group, g.Collapsed, ListTable.CheckedState(g.Group.Rows.Select(r => r.Id), checkedIds), connected, compact, callbacks),
            RowItem r => ContainerRowView.Row(r.Row, r.Project, checkedIds.Contains(r.Row.Id), connected, compact, StatsSummary.CpuCell(r.Row, state.Stats), callbacks),
            _ => throw new InvalidOperationException("A new list item needs its row here: " + item.GetType().Name),
        });
    }
}

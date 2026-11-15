using System.Collections.Immutable;
using Berth.Components;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Berth.Settings;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>
/// The containers of the engine, grouped by compose project, with search, a "running only" switch, actions per row, per project and
/// for the checked rows, and a confirmation before anything is deleted.
/// </summary>
internal sealed class ContainersPage : EngineStateComponent
{
    /// <summary>One line of the virtualized list: a compose project's row or a container's row.</summary>
    private abstract record ListItem(string Key);

    private sealed record GroupItem(ContainerGroup Group, bool Collapsed) : ListItem("group:" + Group.Project);

    private sealed record RowItem(ContainerRow Row, string? Project) : ListItem(Row.Id);

    private static readonly ImmutableHashSet<string> NoIds = ImmutableHashSet<string>.Empty;

    public override Element Render()
    {
        var state = UseEngineState();
        // A narrow window has no room for a button per action: Restart and Pause then go into a menu on each row.
        var compact = !UseBreakpoint(ContainerRowView.WideLayoutMinWidth);
        var settings = UseExternalStore(SettingsHook.Subscribe, SettingsHook.Snapshot);
        var (search, setSearch) = UseState("");
        var (checkedIds, updateChecked) = UseReducer(NoIds);
        var (collapsed, updateCollapsed) = UseReducer(NoIds);
        var (deleteRequest, setDeleteRequest) = UseState<DeleteRequest?>(null);
        var (searchFocused, setSearchFocused) = UseState(false);
        var searchBox = this.UseElementRef<TextBox>();
        var nav = UseNavigation<AppRoute>();
        // Each return to the page (it is kept while a detail page shows) mounts a new search box with the kept text. A box that was
        // kept drops the first edit after the return: Reactor waits for the echo of a write it made while the page was away.
        var (visits, updateVisits) = UseReducer(0);
        UseNavigationLifecycle(onNavigatedTo: _ => updateVisits(v => v + 1));

        var connected = state.Status == ConnectionStatus.Connected;
        var runningOnly = settings.ContainerList.RunningOnly;
        var sort = settings.ContainerList.Sort;
        var filter = UseMemo(() => new ContainerFilter(search, runningOnly), search, runningOnly);
        var groups = UseMemo(() => sort.Apply(ComposeGrouping.Build(state.Containers, state.Pending, filter)), state.Containers, state.Pending, filter, sort);
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
            project => updateCollapsed(current => current.Contains(project) ? current.Remove(project) : current.Add(project)),
            rows => setDeleteRequest(DeleteRequest.For(rows)),
            row => nav.Navigate(new AppRoute.ContainerDetail(row.Id), new NavigateOptions { Transition = NavigationTransition.DrillIn() }));

        var shownIds = visibleRows.Select(r => r.Id).ToArray();
        var deleteRows = DeleteRows(visibleRows, checkedIds, connected);

        // Keyboard shortcuts while the focus is on the page. None of them acts while the delete confirmation is open; Delete and
        // Escape leave the search box's own keys alone (Delete there deletes text), and Escape only clears it.
        var dialogOpen = deleteRequest is not null;
        Command[] shortcuts =
        [
            new()
            {
                Label = "Search",
                Execute = () => FocusSearch(searchBox.Current),
                CanExecute = !dialogOpen,
                Accelerator = Accelerator(VirtualKey.F, VirtualKeyModifiers.Control),
            },
            new()
            {
                Label = "Reload",
                Execute = () => AppServices.Current.Session.Dispatch(new ReloadRequested()),
                CanExecute = !dialogOpen && connected,
                Accelerator = Accelerator(VirtualKey.F5),
            },
            new()
            {
                Label = "Delete selected",
                Execute = () => callbacks.RequestDelete(deleteRows),
                CanExecute = !dialogOpen && !searchFocused && deleteRows.Length > 0,
                Accelerator = Accelerator(VirtualKey.Delete),
            },
            new()
            {
                Label = "Clear search",
                Execute = () => setSearch(""),
                CanExecute = !dialogOpen && searchFocused && search.Length > 0,
                Accelerator = Accelerator(VirtualKey.Escape),
            },
        ];

        var page = (FlexColumn(
                Widgets.PageTitle("Containers").Flex(shrink: 0),
                ConnectionBar.Render(state, () => AppServices.Current.Session.Dispatch(new ConnectRequested())),
                Toolbar(search, setSearch, runningOnly, searchBox, setSearchFocused, visits),
                BulkBar(visibleRows, checkedIds, deleteRows, connected, callbacks),
                Table(state, filter, sort, groups, items, shownIds, checkedIds, connected, compact, callbacks),
                DeleteDialog.Render(deleteRequest, () => setDeleteRequest(null)))
            with
            {
                RowGap = 16,
            })
            .FlexPadding(24, 16, 24, 0);

        return CommandHost(shortcuts, page).Flex(grow: 1, basis: 0);
    }

    /// <summary>The checked rows the Delete button and the Delete key ask to remove: shown, not pending, and none while not connected.</summary>
    private static ContainerRow[] DeleteRows(ContainerRow[] visibleRows, ImmutableHashSet<string> checkedIds, bool connected)
    {
        var ids = BulkSelection.DeleteIds(visibleRows, checkedIds, connected);
        return [.. visibleRows.Where(r => ids.Contains(r.Id))];
    }

    /// <summary>Ctrl+F: the focus goes to the search box with its text selected, so typing replaces it.</summary>
    private static void FocusSearch(TextBox? box)
    {
        if (box is null)
            return;
        box.Focus(FocusState.Keyboard);
        box.SelectAll();
    }

    /// <summary>Sends <paramref name="action"/> for <paramref name="ids"/> to the session current at the time of the click.</summary>
    private static void Send(IReadOnlyList<string> ids, ContainerAction action)
    {
        if (ids.Count > 0)
            AppServices.Current.Session.Dispatch(new ActionRequested([.. ids], action));
    }

    private static void SetRunningOnly(bool value) =>
        AppServices.Current.UpdateSettings(settings => settings with { ContainerList = settings.ContainerList with { RunningOnly = value } });

    /// <summary>A click on a column title: sorts by that column, or reverses it when it is the one sorted by; kept in the settings.</summary>
    private static void SortBy(SortColumn column) =>
        AppServices.Current.UpdateSettings(settings => settings with
        {
            ContainerList = settings.ContainerList with { Sort = settings.ContainerList.Sort.Toggle(column) },
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

    private static Element Toolbar(string search, Action<string> setSearch, bool runningOnly, ElementRef<TextBox> searchBox, Action<bool> setSearchFocused, int visit) =>
        (FlexRow(
                TextBox(search, setSearch, placeholderText: "Search")
                    .Ref(searchBox)
                    .AutomationId("search")
                    .AutomationName("Search")
                    .ToolTip("Search by name, image or ID (Ctrl+F)")
                    .OnGotFocus((_, _) => setSearchFocused(true))
                    .OnLostFocus((_, _) => setSearchFocused(false))
                    .Width(320)
                    .WithKey("search-" + visit),
                ToggleSwitch(runningOnly, SetRunningOnly, onContent: "Only show running containers", offContent: "Only show running containers")
                    .AutomationName("Only show running containers"))
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 24,
            })
            .Flex(shrink: 0);

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

        return (FlexRow(
                Body($"{count} selected").Foreground(Theme.PrimaryText),
                Button("Start", () => callbacks.Send(start, ContainerAction.Start)).IsEnabled(!start.IsEmpty),
                Button("Stop", () => callbacks.Send(stop, ContainerAction.Stop)).IsEnabled(!stop.IsEmpty),
                Button("Delete", () => callbacks.RequestDelete(deleteRows)).ToolTip("Delete the selected containers (Delete)").IsEnabled(deleteRows.Length > 0))
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 8,
            })
            .Flex(shrink: 0);
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
            // The connection's first list has not arrived: an empty list here does not yet mean there are no containers.
            return (FlexColumn(ProgressRing().Size(32, 32).AutomationName("Loading containers")) with { AlignItems = FlexAlign.Center })
                .Margin(0, 24, 0, 0)
                .Flex(grow: 1, basis: 0);
        }

        if (groups.Count == 0)
        {
            return Body(BulkSelection.EmptyText(engineHasContainers: !state.Containers.IsEmpty, filter)).Foreground(Theme.SecondaryText).Margin(12, 8, 0, 0).Flex(grow: 1, basis: 0);
        }

        var header = ContainerRowView.Header(CheckedState(shownIds, checkedIds), connected, compact, value => callbacks.SetChecked(shownIds, value), sort, SortBy);

        var list = ListView(items, item => item.Key, (item, _) => item switch
            {
                GroupItem g => ContainerRowView.Group(g.Group, g.Collapsed, CheckedState(g.Group.Rows.Select(r => r.Id), checkedIds), connected, compact, callbacks).WithKey(g.Key),
                RowItem r => ContainerRowView.Row(r.Row, r.Project, checkedIds.Contains(r.Row.Id), connected, compact, callbacks).WithKey(r.Key),
                _ => throw new InvalidOperationException("A new list item needs its row here: " + item.GetType().Name),
            })
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

    /// <summary>True when every one of <paramref name="ids"/> is checked, false when none is, null when some are.</summary>
    private static bool? CheckedState(IEnumerable<string> ids, ImmutableHashSet<string> checkedIds)
    {
        int all = 0, on = 0;
        foreach (var id in ids)
        {
            all++;
            if (checkedIds.Contains(id))
                on++;
        }

        return on == 0 ? false : on == all ? true : null;
    }
}

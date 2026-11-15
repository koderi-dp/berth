using System.Collections.Immutable;
using Berth.Components;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Berth.Settings;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>What the delete confirmation is about: the ids asked for and the text that names them.</summary>
    private sealed record DeleteRequest(ImmutableArray<string> Ids, string Text);

    private static readonly ImmutableHashSet<string> NoIds = ImmutableHashSet<string>.Empty;

    private static readonly ContainerFilter NoFilter = new("", RunningOnly: false);

    public override Element Render()
    {
        var state = UseEngineState();
        var settings = UseExternalStore(SettingsHook.Subscribe, SettingsHook.Snapshot);
        var (search, setSearch) = UseState("");
        var (checkedIds, updateChecked) = UseReducer(NoIds);
        var (collapsed, updateCollapsed) = UseReducer(NoIds);
        var (deleteRequest, setDeleteRequest) = UseState<DeleteRequest?>(null);

        var connected = state.Status == ConnectionStatus.Connected;
        var runningOnly = settings.ContainerList.RunningOnly;
        var filter = UseMemo(() => new ContainerFilter(search, runningOnly), search, runningOnly);
        var groups = UseMemo(() => ComposeGrouping.Build(state.Containers, state.Pending, filter), state.Containers, state.Pending, filter);
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
            rows => setDeleteRequest(new DeleteRequest([.. rows.Select(r => r.Id)], BulkSelection.DeleteText(rows))));

        var shownIds = visibleRows.Select(r => r.Id).ToArray();

        return (FlexColumn(
                Title("Containers").Flex(shrink: 0),
                ConnectionBar.Render(state, () => AppServices.Current.Session.Dispatch(new ConnectRequested())),
                Toolbar(search, setSearch, runningOnly),
                BulkBar(visibleRows, checkedIds, connected, callbacks),
                Table(state, filter, groups, items, shownIds, checkedIds, connected, callbacks),
                DeleteDialog(deleteRequest, () => setDeleteRequest(null)))
            with
            {
                RowGap = 16,
            })
            .FlexPadding(24, 16, 24, 0)
            .Flex(grow: 1, basis: 0);
    }

    /// <summary>Sends <paramref name="action"/> for <paramref name="ids"/> to the session current at the time of the click.</summary>
    private static void Send(IReadOnlyList<string> ids, ContainerAction action) => Send(ids, action, force: false);

    private static void Send(IReadOnlyList<string> ids, ContainerAction action, bool force)
    {
        if (ids.Count > 0)
            AppServices.Current.Session.Dispatch(new ActionRequested([.. ids], action, force));
    }

    private static void SetRunningOnly(bool value) =>
        AppServices.Current.UpdateSettings(settings => settings with { ContainerList = settings.ContainerList with { RunningOnly = value } });

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

    private static Element Toolbar(string search, Action<string> setSearch, bool runningOnly) =>
        (FlexRow(
                TextBox(search, setSearch, placeholderText: "Search")
                    .AutomationName("Search")
                    .Width(320),
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
    private static Element? BulkBar(ContainerRow[] visibleRows, ImmutableHashSet<string> checkedIds, bool connected, ContainerListCallbacks callbacks)
    {
        var count = BulkSelection.SelectedCount(visibleRows, checkedIds);
        if (count == 0)
            return null;

        var start = BulkSelection.StartIds(visibleRows, checkedIds, connected);
        var stop = BulkSelection.StopIds(visibleRows, checkedIds, connected);
        var delete = BulkSelection.DeleteIds(visibleRows, checkedIds, connected);
        ContainerRow[] deleteRows = [.. visibleRows.Where(r => delete.Contains(r.Id))];

        return (FlexRow(
                Body($"{count} selected"),
                Button("Start", () => callbacks.Send(start, ContainerAction.Start)).IsEnabled(!start.IsEmpty),
                Button("Stop", () => callbacks.Send(stop, ContainerAction.Stop)).IsEnabled(!stop.IsEmpty),
                Button("Delete", () => callbacks.RequestDelete(deleteRows)).IsEnabled(deleteRows.Length > 0))
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
        IReadOnlyList<ContainerGroup> groups,
        IReadOnlyList<ListItem> items,
        string[] shownIds,
        ImmutableHashSet<string> checkedIds,
        bool connected,
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

        var header = ContainerRowView.Header(CheckedState(shownIds, checkedIds), connected, value => callbacks.SetChecked(shownIds, value));

        var list = ListView(items, item => item.Key, (item, _) => item switch
            {
                GroupItem g => ContainerRowView.Group(g.Group, g.Collapsed, CheckedState(g.Group.Rows.Select(r => r.Id), checkedIds), connected, callbacks).WithKey(g.Key),
                RowItem r => ContainerRowView.Row(r.Row, r.Project, checkedIds.Contains(r.Row.Id), connected, callbacks).WithKey(r.Key),
                _ => throw new InvalidOperationException("A new list item needs its row here: " + item.GetType().Name),
            })
            .Set(view =>
            {
                view.SelectionMode = ListViewSelectionMode.None;
                view.ItemContainerStyle ??= RowContainerStyle();
            });

        return (FlexColumn(
                Border(header).BorderBrush(Theme.DividerStroke).BorderThickness(0, 0, 0, 1).Flex(shrink: 0),
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

    /// <summary>The confirmation before a delete; it is always in the tree and opens while there is a request.</summary>
    private static Element DeleteDialog(DeleteRequest? request, Action onDone)
    {
        return ContentDialog("Delete container?", Body(request?.Text ?? "").TextWrapping(), "Delete") with
        {
            IsOpen = request is not null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            OnClosed = result =>
            {
                if (result == ContentDialogResult.Primary && request is not null)
                    ConfirmDelete(request.Ids);
                onDone();
            },
        };
    }

    /// <summary>
    /// Removes the confirmed containers as they are now, read from the current session when the user confirms: rows that are gone
    /// or became pending while the dialog was open are skipped, running ones are stopped first (force), and nothing is sent when
    /// the connection was lost meanwhile.
    /// </summary>
    private static void ConfirmDelete(ImmutableArray<string> ids)
    {
        var state = AppServices.Current.Session.State;
        var rows = ComposeGrouping.Build(state.Containers, state.Pending, NoFilter).SelectMany(g => g.Rows);
        var removal = BulkSelection.Removal(rows, ids, state.Status == ConnectionStatus.Connected);
        Send(removal.Ids, ContainerAction.Remove, removal.Force);
    }
}

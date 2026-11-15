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

    private sealed record RowItem(ContainerRow Row, bool Indented) : ListItem(Row.Id);

    /// <summary>What the delete confirmation is about. <paramref name="Name"/> is set when it is one container.</summary>
    private sealed record DeleteRequest(ImmutableArray<string> Ids, string? Name, bool AnyRunning);

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

        var runningOnly = settings.ContainerList.RunningOnly;
        var filter = UseMemo(() => new ContainerFilter(search, runningOnly), search, runningOnly);
        var groups = UseMemo(() => ComposeGrouping.Build(state.Containers, state.Pending, filter), state.Containers, state.Pending, filter);
        var items = UseMemo(() => Flatten(groups, collapsed), groups, collapsed);
        // Checked rows can be hidden by the search; the bulk actions still act on them.
        var allRows = UseMemo(
            () => ComposeGrouping.Build(state.Containers, state.Pending, NoFilter).SelectMany(g => g.Rows).ToArray(),
            state.Containers,
            state.Pending);

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
            rows => setDeleteRequest(new DeleteRequest(
                [.. rows.Select(r => r.Id)],
                rows.Count == 1 ? rows[0].Name : null,
                rows.Any(r => r.State != RowState.Stopped))));

        var shownIds = groups.SelectMany(g => g.Rows).Select(r => r.Id).ToArray();

        return (FlexColumn(
                Title("Containers").Flex(shrink: 0),
                Toolbar(search, setSearch, runningOnly),
                BulkBar(allRows, checkedIds, callbacks),
                Table(state, groups, items, shownIds, checkedIds, callbacks),
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
                items.Add(new RowItem(group.Rows[0], Indented: false));
                continue;
            }

            var isCollapsed = collapsed.Contains(project);
            items.Add(new GroupItem(group, isCollapsed));
            if (!isCollapsed)
                items.AddRange(group.Rows.Select(row => new RowItem(row, Indented: true)));
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

    /// <summary>Shown while rows are checked: how many, and Start, Stop and Delete for them.</summary>
    private static Element? BulkBar(ContainerRow[] allRows, ImmutableHashSet<string> checkedIds, ContainerListCallbacks callbacks)
    {
        ContainerRow[] rows = [.. allRows.Where(r => checkedIds.Contains(r.Id))];
        if (rows.Length == 0)
            return null;

        string[] stopped = [.. rows.Where(r => r.State == RowState.Stopped).Select(r => r.Id)];
        string[] active = [.. rows.Where(r => r.State != RowState.Stopped).Select(r => r.Id)];

        return (FlexRow(
                Body($"{rows.Length} selected"),
                Button("Start", () => callbacks.Send(stopped, ContainerAction.Start)).IsEnabled(stopped.Length > 0),
                Button("Stop", () => callbacks.Send(active, ContainerAction.Stop)).IsEnabled(active.Length > 0),
                Button("Delete", () => callbacks.RequestDelete(rows)))
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 8,
            })
            .Flex(shrink: 0);
    }

    private static Element Table(
        EngineState state,
        IReadOnlyList<ContainerGroup> groups,
        IReadOnlyList<ListItem> items,
        string[] shownIds,
        ImmutableHashSet<string> checkedIds,
        ContainerListCallbacks callbacks)
    {
        if (groups.Count == 0)
        {
            var message = state.Containers.IsEmpty ? "No containers on this engine." : "No containers match the search.";
            return Body(message).Foreground(Theme.SecondaryText).Margin(12, 8, 0, 0).Flex(grow: 1, basis: 0);
        }

        var header = ContainerRowView.Header(CheckedState(shownIds, checkedIds), value => callbacks.SetChecked(shownIds, value));

        var list = ListView(items, item => item.Key, (item, _) => item switch
            {
                GroupItem g => ContainerRowView.Group(g.Group, g.Collapsed, CheckedState(g.Group.Rows.Select(r => r.Id), checkedIds), callbacks).WithKey(g.Key),
                RowItem r => ContainerRowView.Row(r.Row, r.Indented, checkedIds.Contains(r.Row.Id), callbacks).WithKey(r.Key),
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
        var text = request is null ? "" : DeleteText(request);
        return ContentDialog("Delete container?", Body(text).TextWrapping(), "Delete") with
        {
            IsOpen = request is not null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            OnClosed = result =>
            {
                if (result == ContentDialogResult.Primary && request is not null)
                    Send(request.Ids, ContainerAction.Remove, force: request.AnyRunning);
                onDone();
            },
        };
    }

    private static string DeleteText(DeleteRequest request)
    {
        var text = request.Name is { } name
            ? $"{name} will be removed. Its data that is not in a volume is lost."
            : $"{request.Ids.Length} containers will be removed. Their data that is not in a volume is lost.";
        if (!request.AnyRunning)
            return text;
        return text + (request.Name is not null ? " It is running and will be stopped first." : " Those that are running will be stopped first.");
    }
}

using System.Collections.Immutable;
using Berth.Components;
using Berth.Hooks;
using Berth.Pages.Volumes;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Navigation;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>
/// The volumes on the engine: a usage line, search and a filter (all, in use, unused), a row per volume with its in-use mark, name (a
/// link to its page), age, size, a "more" menu (View details, Copy name) and Delete; Create, Remove unused volumes and a bulk Delete,
/// which confirm what they remove (saying the data is lost for good) and show the batch's progress and result above the list. <c>App</c>
/// sends <see cref="VolumesWanted"/> and asks for the disk use each time this page is shown.
/// </summary>
internal sealed class VolumesPage : ListPageComponent
{
    /// <summary>The page's one dialog at a time.</summary>
    private abstract record Dialog;

    private sealed record CreateDialogOpen : Dialog;

    /// <summary>The filter's choices, in <see cref="UsageShow"/>'s order.</summary>
    private static readonly string[] ShowChoices = ["All volumes", "In use", "Unused"];

    private static readonly object NoBatch = new();

    public override Element Render()
    {
        var state = UseEngineState();
        var compactToolbar = !UseBreakpoint(ListPage.ToolbarWideMinWidth);
        var (view, updateView) = UseListView(AppRoute.Volumes, new ResourceListView<UsageFilter, VolumeColumn>(UsageFilter.All, VolumeRows.DefaultSort));
        var (filter, sort) = (view.Filter, view.Sort);
        var searchBox = UseListSearch();
        var nav = UseNavigation<AppRoute>();
        var (dialog, setDialog) = UseState<Dialog?>(null);
        // A switch of connection closes the create dialog (its create belonged to the old engine).
        UseSessionReset(() => setDialog(null));
        var handlers = UseMemo(() => new VolumeTableHandlers(), Array.Empty<object>());

        var connected = state.Status == ConnectionStatus.Connected;
        var all = UseMemo(() => VolumeRows.From(state), state.Volumes, state.Containers, state.DiskUsage.Usage);
        var rows = UseMemo(() => VolumeRows.Shown(all, filter, sort), all, filter, sort);
        var (checkedKeys, setChecked) = UseCheckedKeys(all);

        var deleteKeys = RowSelection.Checked(rows, checkedKeys, connected);
        var removal = state.Volumes.Removal;
        var removalPanel = UseMemo(() => RemovalPanel.Render(removal, ResourceKind.Volumes), removal ?? NoBatch);
        // A delete plans from the rows shown (the filter and search), when asked and again at confirm.
        var remove = UseRemoveDialogs(state, (now, keys) =>
            Confirmations.RemoveVolumes(VolumeRows.Shown(VolumeRows.From(now), view.Filter, view.Sort), keys, now.Status == ConnectionStatus.Connected, now.DiskUsage.Usage));

        handlers.SetChecked = setChecked;
        handlers.SortBy = column => updateView(v => v with { Sort = v.Sort.Toggle(column) });
        handlers.Act = (row, action) =>
        {
            switch (action.Kind)
            {
                case RowActionKind.ViewDetails:
                    nav.Navigate(new AppRoute.VolumeDetail(row.Name, AppServices.Current.SessionNumber), new NavigateOptions { Transition = NavigationTransition.DrillIn() });
                    break;
                case RowActionKind.CopyName:
                    ContainerParts.CopyToClipboard(row.Name);
                    break;
                case RowActionKind.Delete:
                    remove.AskDelete((string[])[row.Key]);
                    break;
            }
        };

        var keys = ListPage.Keys(searchBox, dialogOpen: dialog is not null || remove.Dialog is not null, connected, deleteKeys.Length > 0 ? () => remove.AskDelete(deleteKeys) : null);
        UseEffect(() => ShellKeys.Register(AppRoute.Volumes, keys), keys);

        var toolbar = ListPage.Toolbar(
            ListPage.SearchBox(filter.Search, value => updateView(v => v with { Filter = v.Filter with { Search = value } }), searchBox, "Search by name (Ctrl+F)"),
            ComboBox(ShowChoices, (int)filter.Show, index => updateView(v => v with { Filter = v.Filter with { Show = (UsageShow)Math.Max(0, index) } }))
                .AutomationId("volume-filter")
                .AutomationName("Show")
                .MinWidth(160),
            compactToolbar,
            new ToolbarAction("Create", "create-volume", () => setDialog(new CreateDialogOpen()), connected),
            new ToolbarAction(
                "Remove unused volumes…",
                "remove-unused-volumes",
                () => remove.AskUnused(Confirmations.PruneVolumes(state, listNarrowed: filter.MayHideUnused)),
                connected && removal is not { IsRunning: true },
                "Remove every volume no container uses, and the data in it"));

        var bulk = ListPage.BulkBar(
            RowSelection.SelectedCount(rows, checkedKeys),
            Button("Delete", () => remove.AskDelete(deleteKeys))
                .AutomationId("delete-selected")
                .ToolTip("Delete the selected volumes (Delete)")
                .IsEnabled(deleteKeys.Length > 0));

        return ListPage.Render(
            "Volumes",
            state,
            ListPage.UsageLine(VolumeRows.UsageLine(state), "volume-usage", state),
            toolbar,
            ListPage.Bars(removalPanel, bulk),
            Table(state, rows, filter, checkedKeys, connected, sort, handlers),
            remove.Dialog ?? DialogOf(dialog, () => setDialog(null)));
    }

    private static Element Table(
        EngineState state,
        IReadOnlyList<VolumeRow> rows,
        UsageFilter filter,
        ImmutableHashSet<string> checkedKeys,
        bool connected,
        ListSort<VolumeColumn> sort,
        VolumeTableHandlers handlers)
    {
        var volumes = state.Volumes;
        return ListTable.States(volumes, rows.Count, "Loading volumes", new VolumesWanted(), VolumeRows.EmptyText(!volumes.Items.IsEmpty, filter))
            ?? Component<VolumeTable, VolumeTableProps>(new(rows, checkedKeys, connected, state.DiskUsage.Usage is not null, sort, handlers)).Flex(grow: 1, basis: 0);
    }

    /// <summary>The dialog open, or an empty element in its place (the page's dialog slot is always there).</summary>
    private static Element DialogOf(Dialog? dialog, Action close) => dialog switch
    {
        CreateDialogOpen => Component<CreateVolumeDialog, CreateVolumeDialogProps>(new(close)),
        _ => Empty(),
    };
}

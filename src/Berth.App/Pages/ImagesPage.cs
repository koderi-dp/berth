using System.Collections.Immutable;
using Berth.Components;
using Berth.Hooks;
using Berth.Pages.Images;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>
/// The images on the engine: a usage line, search and a filter (all, in use, unused, dangling), the pulls under way and a row per tag
/// or untagged image with Run, a "more" menu (Pull again, Inspect, Copy name, Copy ID) and Delete; Pull image, Remove unused images and
/// a bulk Delete, which confirm what they remove and show the batch's progress and result above the list. <c>App</c> sends
/// <see cref="ImagesWanted"/> and asks for the disk use each time this page is shown.
/// </summary>
internal sealed class ImagesPage : ListPageComponent
{
    /// <summary>The page's one dialog at a time (WinUI shows one content dialog at a time).</summary>
    private abstract record Dialog;

    private sealed record PullDialogOpen : Dialog;

    private sealed record RunDialogOpen(ImageRow Row) : Dialog;

    private sealed record InspectDialogOpen(ImageRow Row) : Dialog;

    /// <summary>A delete: the keys the user named and the plan's text when the dialog opened.</summary>
    private sealed record DeleteDialogOpen(ImmutableArray<string> Keys, RemovalPlan Plan) : Dialog;

    private sealed record PruneDialogOpen(PrunePlan Plan) : Dialog;

    /// <summary>The filter's choices, in <see cref="ImageShow"/>'s order.</summary>
    private static readonly string[] ShowChoices = ["All images", "In use", "Unused", "Dangling"];

    private static readonly ImmutableHashSet<string> NoKeys = ImmutableHashSet<string>.Empty;

    private static readonly PullRowsProps NoPullFocus = new(null, 0);

    public override Element Render()
    {
        var state = UseEngineState();
        var compact = !UseBreakpoint(ImageTable.WideLayoutMinWidth);
        var (view, updateView) = UseListView(AppRoute.Images, new ResourceListView<ImageFilter, ImageColumn>(ImageFilter.All, ImageRows.DefaultSort));
        var (filter, sort) = (view.Filter, view.Sort);
        var searchBox = UseListSearch();
        var nav = UseNavigation<AppRoute>();
        var (dialog, setDialog) = UseState<Dialog?>(null);
        var (pullFocus, setPullFocus) = UseState(NoPullFocus);
        var handlers = UseMemo(() => new ImageTableHandlers(), Array.Empty<object>());

        var connected = state.Status == ConnectionStatus.Connected;
        var all = UseMemo(() => ImageRows.From(state), state.Images, state.Containers);
        var rows = UseMemo(() => ImageRows.Shown(all, filter, sort), all, filter, sort);
        var (checkedKeys, setChecked) = UseCheckedKeys(all);

        var deleteKeys = RowSelection.Checked(rows, checkedKeys, connected);
        var removal = state.Images.Removal;
        // A refused image is named by its tags, read from the list while it is still listed.
        var removalPanel = UseMemo(() => RemovalPanel.Render(removal, ResourceKind.Images, key => ImageRows.NameOfKey(state, key)), (object?)removal ?? NoKeys, state.Images.Items);

        void AskDelete(IEnumerable<string> keys)
        {
            var named = keys.ToImmutableArray();
            var plan = Confirmations.RemoveImages(rows, all, named, connected, state.DiskUsage.Usage);
            if (!plan.IsEmpty)
                setDialog(new DeleteDialogOpen(named, plan));
        }

        void FocusPull(string key) => setPullFocus(new PullRowsProps(key, pullFocus.FocusRequest + 1));

        void PullAgain(string key)
        {
            if (state.Pulls.TryGetValue(key, out var pull) && pull.Status == PullStatus.Running)
                FocusPull(key);
            else
                AppServices.Current.Session.Dispatch(new PullRequested(key));
        }

        handlers.SetChecked = setChecked;
        handlers.SortBy = column => updateView(v => v with { Sort = v.Sort.Toggle(column) });
        handlers.Act = (row, action) =>
        {
            switch (action.Kind)
            {
                case RowActionKind.Run:
                    setDialog(new RunDialogOpen(row));
                    break;
                case RowActionKind.PullAgain:
                    PullAgain(row.Key);
                    break;
                case RowActionKind.Inspect:
                    setDialog(new InspectDialogOpen(row));
                    break;
                case RowActionKind.CopyName:
                    ContainerParts.CopyToClipboard(row.Key);
                    break;
                case RowActionKind.CopyId:
                    ContainerParts.CopyToClipboard(row.ImageId);
                    break;
                case RowActionKind.Delete:
                    AskDelete((string[])[row.Key]);
                    break;
            }
        };

        var keys = ListPage.Keys(searchBox, dialogOpen: dialog is not null, connected, deleteKeys.Length > 0 ? () => AskDelete(deleteKeys) : null);
        UseEffect(() => ShellKeys.Register(AppRoute.Images, keys), keys);

        void OpenContainer(string? id)
        {
            setDialog(null);
            if (id is not null)
                nav.Navigate(new AppRoute.ContainerDetail(id, AppServices.Current.SessionNumber), new NavigateOptions { Transition = NavigationTransition.DrillIn() });
        }

        var toolbar = ListPage.Toolbar(
            ListPage.SearchBox(filter.Search, value => updateView(v => v with { Filter = v.Filter with { Search = value } }), searchBox, "Search by name, tag or ID (Ctrl+F)"),
            ComboBox(ShowChoices, (int)filter.Show, index => updateView(v => v with { Filter = v.Filter with { Show = (ImageShow)Math.Max(0, index) } }))
                .AutomationId("image-filter")
                .AutomationName("Show")
                .MinWidth(160),
            Button("Pull image", () => setDialog(new PullDialogOpen()))
                .AccentButton()
                .AutomationId("pull-image")
                .IsEnabled(connected),
            Button("Remove unused images…", () => setDialog(new PruneDialogOpen(Confirmations.PruneImages(state, danglingOnly: false))))
                .AutomationId("remove-unused-images")
                .ToolTip("Remove every image no container uses")
                .IsEnabled(connected && removal is not { IsRunning: true }));

        var selected = RowSelection.SelectedCount(rows, checkedKeys);
        var bulk = ListPage.BulkBar(
            selected,
            Button("Delete", () => AskDelete(deleteKeys))
                .AutomationId("delete-selected")
                .ToolTip("Delete the selected images (Delete)")
                .IsEnabled(deleteKeys.Length > 0));
        return ListPage.Render(
            "Images",
            state,
            ListPage.UsageLine(ImageRows.UsageLine(state), "image-usage", state),
            toolbar,
            ListPage.Bars(removalPanel, bulk),
            Table(state, rows, filter, checkedKeys, compact, connected, sort, pullFocus, handlers),
            DialogOf(dialog, () => setDialog(null), FocusPull, OpenContainer, view));
    }

    private static Element Table(
        EngineState state,
        IReadOnlyList<ImageRow> rows,
        ImageFilter filter,
        ImmutableHashSet<string> checkedKeys,
        bool compact,
        bool connected,
        ListSort<ImageColumn> sort,
        PullRowsProps pullFocus,
        ImageTableHandlers handlers)
    {
        var images = state.Images;
        if (rows.Count == 0 && !images.Loaded)
        {
            // The connection's first list has not arrived (or its read failed): an empty list here does not mean there are no images.
            return images.LoadError is { } error
                ? ListTable.LoadFailed(error, () => AppServices.Current.Session.Dispatch(new ImagesWanted()))
                : ListTable.Loading("Loading images");
        }

        if (rows.Count == 0)
        {
            // A first pull on an empty engine still shows its row.
            return (FlexColumn(
                    Component<PullRows, PullRowsProps>(pullFocus).Flex(shrink: 0),
                    ListTable.EmptyState(ImageRows.EmptyText(!images.Items.IsEmpty, filter)))
                with
                {
                    RowGap = 8,
                })
                .Flex(grow: 1, basis: 0);
        }

        return Component<ImageTable, ImageTableProps>(new(rows, checkedKeys, compact, connected, sort, pullFocus, handlers)).Flex(grow: 1, basis: 0);
    }

    /// <summary>The dialog open, or an empty element in its place (the page's dialog slot is always there).</summary>
    private static Element DialogOf(
        Dialog? dialog,
        Action close,
        Action<string> focusPull,
        Action<string?> openContainer,
        ResourceListView<ImageFilter, ImageColumn> view) => dialog switch
    {
        PullDialogOpen => Component<PullDialog, PullDialogProps>(new(focusPull, close)),
        RunDialogOpen run => Component<RunDialog, RunDialogProps>(new(run.Row.IsTagged ? run.Row.Key : run.Row.ImageId, run.Row.ImageId, openContainer)),
        InspectDialogOpen inspect => Component<InspectDialog, InspectDialogProps>(new(inspect.Row.DisplayName, inspect.Row.ImageId, close)),
        DeleteDialogOpen delete => RemoveDialog.Render(delete.Plan.Title, delete.Plan.Text, canConfirm: true, () => ConfirmDelete(delete.Keys, view), close),
        PruneDialogOpen prune => RemoveDialog.Render(prune.Plan.Title, prune.Plan.Text, !prune.Plan.IsEmpty, () => RemoveDialog.SendUnused(prune.Plan), close),
        _ => Empty(),
    };

    /// <summary>
    /// Sends the confirmed delete as it stands now, from the current session: of the keys named, the rows the filter and search show,
    /// still listed and not pending, grouped by image as <see cref="Confirmations.RemoveImages"/> decides; nothing while not connected.
    /// </summary>
    private static void ConfirmDelete(ImmutableArray<string> keys, ResourceListView<ImageFilter, ImageColumn> view)
    {
        var state = AppServices.Current.Session.State;
        var all = ImageRows.From(state);
        RemoveDialog.Send(Confirmations.RemoveImages(ImageRows.Shown(all, view.Filter, view.Sort), all, keys, state.Status == ConnectionStatus.Connected, state.DiskUsage.Usage));
    }
}

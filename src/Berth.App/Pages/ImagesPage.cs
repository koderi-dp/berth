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

    /// <summary>The filter's choices, in <see cref="ImageShow"/>'s order.</summary>
    private static readonly string[] ShowChoices = ["All images", "In use", "Unused", "Dangling"];

    private static readonly ImmutableHashSet<string> NoKeys = ImmutableHashSet<string>.Empty;

    private static readonly PullRowsProps NoPullFocus = new(null, 0);

    public override Element Render()
    {
        var state = UseEngineState();
        var compact = !UseBreakpoint(ImageTable.WideLayoutMinWidth);
        var compactToolbar = !UseBreakpoint(ListPage.ToolbarWideMinWidth);
        var (view, updateView) = UseListView(AppRoute.Images, new ResourceListView<ImageFilter, ImageColumn>(ImageFilter.All, ImageRows.DefaultSort));
        var (filter, sort) = (view.Filter, view.Sort);
        var searchBox = UseListSearch();
        var nav = UseNavigation<AppRoute>();
        var (dialog, setDialog) = UseState<Dialog?>(null);
        var (pullFocus, setPullFocus) = UseState(NoPullFocus);
        var handlers = UseMemo(() => new ImageTableHandlers(), Array.Empty<object>());
        // The pull whose Cancel was clicked: the engine may still finish it, so a note says so until it is closed.
        var (cancelledPull, setCancelledPull) = UseState<string?>(null);
        var onPullCancelled = UseMemo(() => new Action<string>(reference => handlers.PullCancelled(reference)), Array.Empty<object>());

        var connected = state.Status == ConnectionStatus.Connected;
        var all = UseMemo(() => ImageRows.From(state), state.Images, state.Containers);
        var rows = UseMemo(() => ImageRows.Shown(all, filter, sort), all, filter, sort);
        var (checkedKeys, setChecked) = UseCheckedKeys(all);

        var deleteKeys = RowSelection.Checked(rows, checkedKeys, connected);
        var removal = state.Images.Removal;
        // A refused image is named by its tags, read from the list while it is still listed.
        var removalPanel = UseMemo(() => RemovalPanel.Render(removal, ResourceKind.Images, key => ImageRows.NameOfKey(state, key)), (object?)removal ?? NoKeys, state.Images.Items);

        // A delete plans from the rows shown (the filter and search), grouped by image, when asked and again at confirm.
        var remove = UseRemoveDialogs(state, (now, keys) =>
        {
            var nowAll = ImageRows.From(now);
            return Confirmations.RemoveImages(ImageRows.Shown(nowAll, view.Filter, view.Sort), nowAll, keys, now.Status == ConnectionStatus.Connected, now.DiskUsage.Usage);
        });
        var unusedNotKnown = Confirmations.UnusedNotKnown(state);

        void FocusPull(string key) => setPullFocus(new PullRowsProps(key, pullFocus.FocusRequest + 1));

        void PullAgain(string key)
        {
            if (state.Pulls.TryGetValue(key, out var pull) && pull.Status == PullStatus.Running)
                FocusPull(key);
            else
                AppServices.Current.Session.Dispatch(new PullRequested(key));
        }

        handlers.SetChecked = setChecked;
        handlers.PullCancelled = reference => setCancelledPull(reference);
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
                    remove.AskDelete((string[])[row.Key]);
                    break;
            }
        };

        var keys = ListPage.Keys(searchBox, dialogOpen: dialog is not null || remove.Dialog is not null, connected, deleteKeys.Length > 0 ? () => remove.AskDelete(deleteKeys) : null);
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
            compactToolbar,
            new ToolbarAction("Pull image", "pull-image", () => setDialog(new PullDialogOpen()), connected),
            new ToolbarAction(
                "Remove unused images…",
                "remove-unused-images",
                () => remove.AskUnused(Confirmations.PruneImages(state, danglingOnly: false)),
                connected && removal is not { IsRunning: true } && unusedNotKnown is null,
                unusedNotKnown ?? "Remove every image no container uses"));

        var selected = RowSelection.SelectedCount(rows, checkedKeys);
        var bulk = ListPage.BulkBar(
            selected,
            Button("Delete", () => remove.AskDelete(deleteKeys))
                .AutomationId("delete-selected")
                .ToolTip("Delete the selected images (Delete)")
                .IsEnabled(deleteKeys.Length > 0));
        return ListPage.Render(
            "Images",
            state,
            ListPage.UsageLine(ImageRows.UsageLine(state), "image-usage", state),
            toolbar,
            ListPage.Bars(removalPanel, bulk, CancelledNote(cancelledPull, () => setCancelledPull(null))),
            Table(state, rows, filter, checkedKeys, compact, connected, sort, pullFocus with { OnCancelled = onPullCancelled }, handlers),
            remove.Dialog ?? DialogOf(dialog, () => setDialog(null), FocusPull, OpenContainer));
    }

    /// <summary>After a pull's Cancel: Berth stopped following it, but the engine may still finish downloading the image (it has no cancel).</summary>
    private static Element? CancelledNote(string? reference, Action onClose) =>
        reference is null
            ? null
            : (InfoBar(message: $"Stopped following the pull of {reference}. The engine may still finish downloading the image.") with { IsOpen = true })
                .Informational()
                .IsClosable()
                .Closed(onClose)
                .AutomationId("pull-cancelled")
                .Flex(shrink: 0);

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
        // A first pull on an empty engine still shows its row.
        return ListTable.States(
                images,
                rows.Count,
                "Loading images",
                new ImagesWanted(),
                ImageRows.EmptyText(!images.Items.IsEmpty, filter),
                aboveEmpty: rows.Count == 0 ? Component<PullRows, PullRowsProps>(pullFocus) : null)
            ?? Component<ImageTable, ImageTableProps>(new(rows, checkedKeys, compact, connected, sort, pullFocus, handlers)).Flex(grow: 1, basis: 0);
    }

    /// <summary>The dialog open, or an empty element in its place (the page's dialog slot is always there).</summary>
    private static Element DialogOf(
        Dialog? dialog,
        Action close,
        Action<string> focusPull,
        Action<string?> openContainer) => dialog switch
    {
        PullDialogOpen => Component<PullDialog, PullDialogProps>(new(focusPull, close)),
        RunDialogOpen run => Component<RunDialog, RunDialogProps>(new(run.Row.IsTagged ? run.Row.Key : run.Row.ImageId, run.Row.ImageId, openContainer)),
        InspectDialogOpen inspect => Component<InspectDialog, InspectDialogProps>(new(inspect.Row.DisplayName, inspect.Row.ImageId, close)),
        _ => Empty(),
    };
}

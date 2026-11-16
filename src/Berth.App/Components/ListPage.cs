using Berth.Hooks;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>
/// The frame every list page (containers, images, volumes, networks) is assembled from: the page itself with its sections in a fixed
/// order, the toolbar, the search box, the bulk bar and what the page does with the app's shortcuts. The table is
/// <see cref="ListTable"/>; the view state kept across a page being built again is <c>ListPageComponent.UseListView</c>.
/// </summary>
internal static class ListPage
{
    /// <summary>The search box's width in the toolbar.</summary>
    private const double SearchWidth = 320;

    /// <summary>
    /// The page: <paramref name="title"/>, the connection bar while not connected, the optional <paramref name="summary"/> line
    /// (totals, disk use), the <paramref name="toolbar"/>, the <paramref name="bulkBar"/> while rows are checked, the
    /// <paramref name="table"/> (or its loading or empty state) and the page's <paramref name="dialogs"/>, which stay in the tree
    /// and open themselves. The page registers its shortcuts itself (<see cref="Keys"/>, <see cref="ShellKeys.Register"/>).
    /// </summary>
    /// <remarks>
    /// Every section is keyed: the connection bar, the summary and the bulk bar come and go, and unkeyed children are matched by
    /// position, so without keys the toolbar would be mounted again (with a new, empty search box) each time one of them changes.
    /// Dialogs are keyed by their place in <paramref name="dialogs"/>: pass the same dialogs in the same order on every render (each
    /// one always, open or not).
    /// </remarks>
    public static Element Render(
        string title,
        EngineState state,
        Element? summary,
        Element toolbar,
        Element? bulkBar,
        Element table,
        params Element[] dialogs)
    {
        var sections = new List<Element?>
        {
            Widgets.PageTitle(title).Flex(shrink: 0).WithKey("title"),
            ConnectionBar.Render(state, () => AppServices.Current.Session.Dispatch(new ConnectRequested()))?.WithKey("connection"),
            summary?.WithKey("summary"),
            toolbar.WithKey("toolbar"),
            bulkBar?.WithKey("bulk"),
            table.WithKey("table"),
        };
        for (var i = 0; i < dialogs.Length; i++)
            sections.Add(dialogs[i].WithKey("dialog-" + i));

        return (FlexColumn([.. sections])
            with
            {
                RowGap = 16,
            })
            .FlexPadding(24, 16, 24, 0)
            .Flex(grow: 1, basis: 0);
    }

    /// <summary>
    /// The row under the title: the <paramref name="search"/> box, an optional <paramref name="filter"/> control beside it, and
    /// the page's <paramref name="buttons"/> (such as Create) at the right end.
    /// </summary>
    public static Element Toolbar(Element search, Element? filter, params Element[] buttons)
    {
        if (buttons.Length == 0)
        {
            return (FlexRow(search, filter)
                with
                {
                    AlignItems = FlexAlign.Center,
                    ColumnGap = 24,
                })
                .Flex(shrink: 0);
        }

        // A grid, not a flex row: a flex row of buttons takes the whole width it is offered, which left the search box and the filter
        // none. The buttons take what they need at the right end; the search and the filter keep their widths at the left.
        return Grid(
                ToolbarColumns,
                ToolbarRows,
                search.VAlign(VerticalAlignment.Center).Margin(0, 0, 24, 0).Grid(column: 0),
                filter?.VAlign(VerticalAlignment.Center).Margin(0, 0, 24, 0).Grid(column: 1),
                HStack(8, buttons).VAlign(VerticalAlignment.Center).Grid(column: 3))
            .Flex(shrink: 0);
    }

    private static readonly GridSize[] ToolbarColumns = [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto];

    private static readonly GridSize[] ToolbarRows = [GridSize.Auto];

    /// <summary>
    /// The list's search box (AutomationId <c>search</c>): uncontrolled (<see cref="Widgets.SearchBox"/>), so a page that comes
    /// back from the navigation cache, or a box mounted again, starts from <paramref name="search"/> and the box and the filter
    /// cannot disagree. Escape empties it. <paramref name="tooltip"/> says what it searches and names Ctrl+F.
    /// </summary>
    public static Element SearchBox(string search, Action<string> setSearch, ListSearch box, string tooltip) =>
        Widgets.SearchBox(search, setSearch, "Search", box.Box, box.SetFocused, clearOnEscape: true)
            .AutomationId("search")
            .AutomationName("Search")
            .ToolTip(tooltip)
            .Width(SearchWidth);

    /// <summary>
    /// Shown while shown rows are checked (<paramref name="count"/> above 0): "{count} selected" and the page's
    /// <paramref name="buttons"/> for them. Null otherwise.
    /// </summary>
    public static Element? BulkBar(int count, params Element[] buttons)
    {
        if (count == 0)
            return null;

        return (FlexRow([Body($"{count} selected").Foreground(Theme.PrimaryText), .. buttons])
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 8,
            })
            .Flex(shrink: 0);
    }

    /// <summary>
    /// The usage line under the title (<paramref name="text"/>: "67 MB in use of 1.2 GB · 12 images", AutomationId
    /// <paramref name="automationId"/>) and a button that reads the disk use again; a ring while it is read.
    /// </summary>
    public static Element UsageLine(string text, string automationId, EngineState state) =>
        (FlexRow(
                Body(text).Foreground(Theme.SecondaryText).AutomationId(automationId),
                state.DiskUsage.Loading
                    ? ProgressRing().Size(16, 16).AutomationName("Reading the disk use")
                    : Widgets.IconButton(Glyphs.Restart, "Read the disk use again", () => AppServices.Current.Session.Dispatch(new DiskUsageRequested()), size: 12)
                        .AutomationId("refresh-usage")
                        .Padding(4)
                        .MinWidth(24)
                        .MinHeight(24)
                        .IsEnabled(state.Status == ConnectionStatus.Connected))
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 8,
            })
            .MinHeight(24)
            .Flex(shrink: 0);

    /// <summary>
    /// The bars above the table: the batch of removes (<see cref="RemovalPanel"/>) above the bulk bar, each keyed; null when neither is
    /// there.
    /// </summary>
    public static Element? Bars(Element? removalPanel, Element? bulkBar) =>
        removalPanel is null && bulkBar is null
            ? null
            : (FlexColumn(removalPanel?.WithKey("removal"), bulkBar?.WithKey("selection")) with { RowGap = 12 }).Flex(shrink: 0);

    /// <summary>
    /// What a list page does with the app's shortcuts: Ctrl+F focuses its search box with the text selected, F5 reloads every list
    /// shown (while <paramref name="connected"/>), and Delete calls <paramref name="deleteSelected"/> (null while nothing checked can
    /// be deleted). None of them while one of its dialogs is open (<paramref name="dialogOpen"/>). Escape in the search box empties it
    /// (the box's own key).
    /// </summary>
    public static PageKeys Keys(ListSearch search, bool dialogOpen, bool connected, Action? deleteSelected) =>
        dialogOpen
            ? new PageKeys()
            : new PageKeys(
                Search: () => FocusSearch(search.Box.Current),
                Reload: connected ? () => AppServices.Current.Session.Dispatch(new ReloadRequested()) : null,
                Delete: deleteSelected);

    /// <summary>Ctrl+F: the focus goes to the search box with its text selected, so typing replaces it.</summary>
    private static void FocusSearch(TextBox? box)
    {
        if (box is null)
            return;
        box.Focus(FocusState.Keyboard);
        box.SelectAll();
    }
}

using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>
/// The frame every list page (containers, images, volumes, networks) is assembled from: the page itself with its sections in a fixed
/// order, the toolbar, the search box, the bulk bar and the keyboard shortcuts. The table is <see cref="ListTable"/>.
/// </summary>
internal static class ListPage
{
    /// <summary>The search box's width in the toolbar.</summary>
    private const double SearchWidth = 320;

    /// <summary>
    /// The page: <paramref name="title"/>, the connection bar while not connected, the optional <paramref name="summary"/> line
    /// (totals, disk use), the <paramref name="toolbar"/>, the <paramref name="bulkBar"/> while rows are checked, the
    /// <paramref name="table"/> (or its loading or empty state) and the page's <paramref name="dialogs"/>, which stay in the tree
    /// and open themselves. <paramref name="shortcuts"/> act while the focus is on the page (<see cref="Shortcuts"/>).
    /// </summary>
    /// <remarks>
    /// Every section is keyed: the connection bar, the summary and the bulk bar come and go, and unkeyed children are matched by
    /// position, so without keys the toolbar would be mounted again (with a new, empty search box) each time one of them changes.
    /// </remarks>
    public static Element Render(
        string title,
        EngineState state,
        Element? summary,
        Element toolbar,
        Element? bulkBar,
        Element table,
        Command[] shortcuts,
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

        var page = (FlexColumn([.. sections])
            with
            {
                RowGap = 16,
            })
            .FlexPadding(24, 16, 24, 0);

        return CommandHost(shortcuts, page).Flex(grow: 1, basis: 0);
    }

    /// <summary>
    /// The row under the title: the <paramref name="search"/> box, an optional <paramref name="filter"/> control beside it, and
    /// the page's <paramref name="buttons"/> (such as Create) at the right end.
    /// </summary>
    public static Element Toolbar(Element search, Element? filter, params Element[] buttons)
    {
        var children = new List<Element?> { search, filter };
        if (buttons.Length > 0)
        {
            children.Add(Empty().Flex(grow: 1, basis: 0));
            children.Add((FlexRow(buttons) with { AlignItems = FlexAlign.Center, ColumnGap = 8 }).Flex(shrink: 0));
        }

        return (FlexRow([.. children])
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 24,
            })
            .Flex(shrink: 0);
    }

    /// <summary>
    /// The list's search box (AutomationId <c>search</c>): uncontrolled (<see cref="Widgets.SearchBox"/>), so a page that comes
    /// back from the navigation cache, or a box mounted again, starts from <paramref name="search"/> and the box and the filter
    /// cannot disagree. Escape empties it. <paramref name="tooltip"/> says what it searches and names Ctrl+F.
    /// </summary>
    public static Element SearchBox(string search, Action<string> setSearch, ElementRef<TextBox> box, Action<bool> setFocused, string tooltip) =>
        Widgets.SearchBox(search, setSearch, "Search", box, setFocused, clearOnEscape: true)
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
    /// The keyboard shortcuts of a list: Ctrl+F focuses <paramref name="searchBox"/> with its text selected, F5 reloads every list
    /// shown (while <paramref name="connected"/>), and Delete calls <paramref name="deleteSelected"/> while
    /// <paramref name="canDelete"/> and the search box does not have the focus (Delete there deletes text). None acts while a
    /// dialog of the page is open (<paramref name="dialogOpen"/>). Escape in the search box empties it (the box's own key).
    /// </summary>
    public static Command[] Shortcuts(ElementRef<TextBox> searchBox, bool dialogOpen, bool connected, bool searchFocused, bool canDelete, Action deleteSelected) =>
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
            Execute = deleteSelected,
            CanExecute = !dialogOpen && !searchFocused && canDelete,
            Accelerator = Accelerator(VirtualKey.Delete),
        },
    ];

    /// <summary>Ctrl+F: the focus goes to the search box with its text selected, so typing replaces it.</summary>
    private static void FocusSearch(TextBox? box)
    {
        if (box is null)
            return;
        box.Focus(FocusState.Keyboard);
        box.SelectAll();
    }
}

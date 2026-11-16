using Berth.Components;
using Berth.Hooks;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Xaml.Controls;

namespace Berth.Pages;

/// <summary>
/// The networks on the engine. For now the scaffold only: title, search, and the loading state until the list arrives (then a count);
/// the table comes next. <c>App</c> sends <see cref="NetworksWanted"/> each time this page is shown.
/// </summary>
internal sealed class NetworksPage : EngineStateComponent
{
    public override Element Render()
    {
        var state = UseEngineState();
        var (search, setSearch) = UseState("");
        var (searchFocused, setSearchFocused) = UseState(false);
        var searchBox = this.UseElementRef<TextBox>();

        var connected = state.Status == ConnectionStatus.Connected;
        var shortcuts = ListPage.Shortcuts(searchBox, dialogOpen: false, connected, searchFocused, canDelete: false, static () => { });

        return ListPage.Render(
            "Networks",
            state,
            summary: null,
            ListPage.Toolbar(ListPage.SearchBox(search, setSearch, searchBox, setSearchFocused, "Search by name, driver or ID (Ctrl+F)"), filter: null),
            bulkBar: null,
            // Until the table is built: the loading state until the list arrives, then how many there are.
            state.Networks.Loaded ? ListTable.EmptyState($"{state.Networks.Items.Length} networks on this engine.") : ListTable.Loading("Loading networks"),
            shortcuts);
    }
}

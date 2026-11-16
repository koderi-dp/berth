using Berth.Components;
using Berth.Hooks;
using Berth.Session;
using Microsoft.UI.Reactor.Core;

namespace Berth.Pages;

/// <summary>
/// The networks on the engine. For now the scaffold only: title, search, and the loading state until the list arrives (then a count);
/// the table comes next. <c>App</c> sends <see cref="NetworksWanted"/> each time this page is shown.
/// </summary>
internal sealed class NetworksPage : ListPageComponent
{
    public override Element Render()
    {
        var state = UseEngineState();
        var (search, setSearch) = UseState("");
        var searchBox = UseListSearch();

        var connected = state.Status == ConnectionStatus.Connected;
        var keys = ListPage.Keys(searchBox, dialogOpen: false, connected, deleteSelected: null);
        UseEffect(() => ShellKeys.Register(AppRoute.Networks, keys), keys);

        var count = state.Networks.Items.Length;
        return ListPage.Render(
            "Networks",
            state,
            summary: null,
            ListPage.Toolbar(
                ListPage.SearchBox(search, setSearch, searchBox, "Search by name, driver or ID (Ctrl+F)"),
                filter: null),
            bulkBar: null,
            // Until the table is built: the loading state until the list arrives, then how many there are.
            state.Networks.Loaded
                ? ListTable.EmptyState(count == 1 ? "1 network on this engine." : $"{count} networks on this engine.")
                : ListTable.Loading("Loading networks"));
    }
}

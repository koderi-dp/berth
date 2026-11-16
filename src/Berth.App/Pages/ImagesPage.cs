using Berth.Components;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor.Core;

namespace Berth.Pages;

/// <summary>
/// The images on the engine. For now the scaffold only: title, search, and the loading state until the list arrives (then a count);
/// the table comes next. <c>App</c> sends <see cref="ImagesWanted"/> each time this page is shown.
/// </summary>
internal sealed class ImagesPage : ListPageComponent
{
    public override Element Render()
    {
        var state = UseEngineState();
        var (view, updateView) = UseListView(AppRoute.Images, new ResourceListView<ImageFilter, ImageColumn>(ImageFilter.All, ImageRows.DefaultSort));
        var searchBox = UseListSearch();

        var connected = state.Status == ConnectionStatus.Connected;
        var keys = ListPage.Keys(searchBox, dialogOpen: false, connected, deleteSelected: null);
        UseEffect(() => ShellKeys.Register(AppRoute.Images, keys), keys);

        var count = state.Images.Items.Length;
        return ListPage.Render(
            "Images",
            state,
            summary: null,
            ListPage.Toolbar(
                ListPage.SearchBox(view.Filter.Search, value => updateView(v => v with { Filter = v.Filter with { Search = value } }), searchBox, "Search by name, tag or ID (Ctrl+F)"),
                filter: null),
            bulkBar: null,
            // Until the table is built: the loading state until the list arrives, then how many there are.
            state.Images.Loaded
                ? ListTable.EmptyState(count == 1 ? "1 image on this engine." : $"{count} images on this engine.")
                : ListTable.Loading("Loading images"));
    }
}

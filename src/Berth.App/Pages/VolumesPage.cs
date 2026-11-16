using Berth.Components;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor.Core;

namespace Berth.Pages;

/// <summary>
/// The volumes on the engine. For now the scaffold only: title, search, and the loading state until the list arrives (then a count);
/// the table comes next. <c>App</c> sends <see cref="VolumesWanted"/> each time this page is shown.
/// </summary>
internal sealed class VolumesPage : ListPageComponent
{
    public override Element Render()
    {
        var state = UseEngineState();
        var (view, updateView) = UseListView(AppRoute.Volumes, new ResourceListView<UsageFilter, VolumeColumn>(UsageFilter.All, VolumeRows.DefaultSort));
        var searchBox = UseListSearch();

        var connected = state.Status == ConnectionStatus.Connected;
        var keys = ListPage.Keys(searchBox, dialogOpen: false, connected, deleteSelected: null);
        UseEffect(() => ShellKeys.Register(AppRoute.Volumes, keys), keys);

        var count = state.Volumes.Items.Length;
        return ListPage.Render(
            "Volumes",
            state,
            summary: null,
            ListPage.Toolbar(
                ListPage.SearchBox(view.Filter.Search, value => updateView(v => v with { Filter = v.Filter with { Search = value } }), searchBox, "Search by name (Ctrl+F)"),
                filter: null),
            bulkBar: null,
            // Until the table is built: the loading state until the list arrives, then how many there are.
            state.Volumes.Loaded
                ? ListTable.EmptyState(count == 1 ? "1 volume on this engine." : $"{count} volumes on this engine.")
                : ListTable.Loading("Loading volumes"));
    }
}

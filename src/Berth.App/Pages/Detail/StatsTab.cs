using Berth.Components;
using Berth.Session;
using Microsoft.UI.Reactor.Core;

namespace Berth.Pages.Detail;

/// <summary>The "Stats" tab of a container's detail page: the container's CPU, memory, disk and network over time. Filled in by a later task; for now it shows its name.</summary>
internal static class StatsTab
{
    public static Element Render(DetailState detail) => Widgets.CenteredNote("Stats");
}

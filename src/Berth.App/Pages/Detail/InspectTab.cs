using Berth.Components;
using Berth.Session;
using Microsoft.UI.Reactor.Core;

namespace Berth.Pages.Detail;

/// <summary>The "Inspect" tab of a container's detail page: the container's settings in sections and the engine's whole answer. Filled in by a later task; for now it shows its name.</summary>
internal static class InspectTab
{
    public static Element Render(DetailState detail) => Widgets.CenteredNote("Inspect");
}

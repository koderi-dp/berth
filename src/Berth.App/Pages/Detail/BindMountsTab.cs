using Berth.Components;
using Berth.Session;
using Microsoft.UI.Reactor.Core;

namespace Berth.Pages.Detail;

/// <summary>The "Bind mounts" tab of a container's detail page: the container's bind mounts. Filled in by a later task; for now it shows its name.</summary>
internal static class BindMountsTab
{
    public static Element Render(DetailState detail) => Widgets.CenteredNote("Bind mounts");
}

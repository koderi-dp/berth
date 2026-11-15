using Berth.Components;
using Berth.Session;
using Microsoft.UI.Reactor.Core;

namespace Berth.Pages.Detail;

/// <summary>The "Logs" tab of a container's detail page: the container's log, followed live, with search, copy and clear. Filled in by the next task; for now it shows its name.</summary>
internal static class LogsTab
{
    public static Element Render(DetailState detail, Action clearLogs) => Widgets.CenteredNote("Logs");
}

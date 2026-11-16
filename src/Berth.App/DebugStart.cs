#if DEBUG
using Berth.Engine.Models;

namespace Berth;

/// <summary>
/// Debug builds only: <c>BERTH_OPEN_EXEC=&lt;container name&gt;</c> opens that container's page on the Exec tab once its row is
/// listed, so a check can reach the terminal without a click. Compiled out of Release.
/// </summary>
internal static class DebugStart
{
    private static string? _containerToOpen = Environment.GetEnvironmentVariable("BERTH_OPEN_EXEC") is { Length: > 0 } name ? name : null;
    private static string? _execTabFor;

    /// <summary>The id of the container named by the switch, once, when <paramref name="containers"/> has it; null otherwise.</summary>
    public static string? TakeContainerToOpen(IReadOnlyList<ContainerSummary> containers)
    {
        if (_containerToOpen is not { } name)
            return null;
        var match = containers.FirstOrDefault(c => c.Names.Any(n => n.TrimStart('/') == name));
        if (match is null)
            return null;
        _containerToOpen = null;
        _execTabFor = match.Id;
        return match.Id;
    }

    /// <summary>Whether the page of <paramref name="containerId"/> opens on the Exec tab: once, for the container the switch opened.</summary>
    public static bool TakeExecTab(string containerId)
    {
        if (_execTabFor != containerId)
            return false;
        _execTabFor = null;
        return true;
    }
}
#endif

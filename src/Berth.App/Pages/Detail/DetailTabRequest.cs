using Berth.Presentation;

namespace Berth.Pages.Detail;

/// <summary>
/// The tab a container's page should show when it next opens: set by "Open terminal" on the list, taken once by the page. UI thread
/// only.
/// </summary>
internal static class DetailTabRequest
{
    private static (string ContainerId, DetailTab Tab)? _request;

    public static void Set(string containerId, DetailTab tab) => _request = (containerId, tab);

    /// <summary>The tab asked for <paramref name="containerId"/>, or null when none was; clears it.</summary>
    public static DetailTab? Take(string containerId)
    {
        if (_request is not { } request || request.ContainerId != containerId)
            return null;
        _request = null;
        return request.Tab;
    }
}

using Berth.Presentation;

namespace Berth.Pages.Detail;

/// <summary>
/// The tab a container's page should show when it next opens: set by "Open terminal" on the list, taken once by the page. It never
/// outlives the navigation it was set for: the next page that becomes active takes it or drops it, and any route that is not a
/// container's page drops it (<see cref="Clear"/>). UI thread only.
/// </summary>
internal static class DetailTabRequest
{
    private static (string ContainerId, DetailTab Tab)? _request;

    public static void Set(string containerId, DetailTab tab) => _request = (containerId, tab);

    /// <summary>The tab asked for <paramref name="containerId"/>, or null when none was; clears any request, its own or not.</summary>
    public static DetailTab? Take(string containerId)
    {
        var request = _request;
        _request = null;
        return request is { } asked && asked.ContainerId == containerId ? asked.Tab : null;
    }

    /// <summary>Drops a request not taken: the route shown is not a container's page.</summary>
    public static void Clear() => _request = null;
}

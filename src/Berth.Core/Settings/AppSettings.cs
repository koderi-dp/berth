using Berth.Connections;
using Berth.Presentation;

namespace Berth.Settings;

/// <summary>How the containers list is shown: whether only running containers are, and how it is sorted.</summary>
public sealed record ContainerListOptions(bool RunningOnly, ContainerSort Sort)
{
    /// <summary>Every container, sorted by name ascending.</summary>
    public static readonly ContainerListOptions Default = new(RunningOnly: false, ContainerSort.Default);
}

/// <summary>Everything in <c>settings.json</c>: the saved connections, which one is active, and the containers list options.</summary>
public sealed record AppSettings(IReadOnlyList<ConnectionProfile> Connections, string? ActiveConnectionId, ContainerListOptions ContainerList);

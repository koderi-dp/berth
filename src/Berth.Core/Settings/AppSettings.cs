using Berth.Connections;

namespace Berth.Settings;

/// <summary>How the containers list is shown.</summary>
public sealed record ContainerListOptions(bool RunningOnly);

/// <summary>Everything in <c>settings.json</c>: the saved connections, which one is active, and the containers list options.</summary>
public sealed record AppSettings(IReadOnlyList<ConnectionProfile> Connections, string? ActiveConnectionId, ContainerListOptions ContainerList);

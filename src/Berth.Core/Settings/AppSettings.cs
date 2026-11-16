using Berth.Connections;
using Berth.Presentation;

namespace Berth.Settings;

/// <summary>How the containers list is shown: whether only running containers are, and how it is sorted.</summary>
public sealed record ContainerListOptions(bool RunningOnly, ContainerSort Sort)
{
    /// <summary>Every container, sorted by name ascending.</summary>
    public static readonly ContainerListOptions Default = new(RunningOnly: false, ContainerSort.Default);
}

/// <summary>How a container's log is shown: with each line's timestamp or not, and long lines wrapped or scrolled sideways.</summary>
public sealed record LogViewOptions(bool Timestamps, bool Wrap)
{
    /// <summary>No timestamps, long lines wrapped.</summary>
    public static readonly LogViewOptions Default = new(Timestamps: false, Wrap: true);
}

/// <summary>Everything in <c>settings.json</c>: the saved connections, which one is active, and the containers list and log options.</summary>
public sealed record AppSettings(IReadOnlyList<ConnectionProfile> Connections, string? ActiveConnectionId, ContainerListOptions ContainerList)
{
    /// <summary>How the detail page's Logs tab shows a log.</summary>
    public LogViewOptions Logs { get; init; } = LogViewOptions.Default;
}

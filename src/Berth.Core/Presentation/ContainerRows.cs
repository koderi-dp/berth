using Berth.Session;

namespace Berth.Presentation;

/// <summary>What the Containers page shows for a container's state: the engine's running, paused and restarting; everything else is stopped.</summary>
public enum RowState { Running, Paused, Restarting, Stopped }

/// <summary>One port as the Containers page shows it, such as "8080:80". <see cref="Url"/> is null when there is nothing to open.</summary>
public sealed record PortLink(string Text, Uri? Url);

/// <summary>One container as the Containers page shows it.</summary>
/// <param name="Pending">The action in progress on the container, if any.</param>
public sealed record ContainerRow(
    string Id,
    string ShortId,
    string Name,
    string Image,
    RowState State,
    string Status,
    IReadOnlyList<PortLink> Ports,
    DateTimeOffset Created,
    ContainerAction? Pending);

/// <summary>
/// A compose project and its containers, or a single container that is not part of compose (<see cref="Project"/> null).
/// </summary>
public sealed record ContainerGroup(string? Project, RowState State, IReadOnlyList<ContainerRow> Rows);

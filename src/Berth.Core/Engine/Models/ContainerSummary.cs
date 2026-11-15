namespace Berth.Engine.Models;

/// <summary>A container port; <see cref="Ip"/> and <see cref="PublicPort"/> are null when the port is not published.</summary>
public sealed record PortMapping(string? Ip, int PrivatePort, int? PublicPort, string Type);

/// <summary>
/// One entry of the container list. <see cref="Names"/> are as the engine reports them, with the leading "/".
/// <see cref="Created"/> is in Unix seconds.
/// </summary>
public sealed record ContainerSummary(
    string Id,
    IReadOnlyList<string> Names,
    string Image,
    string ImageId,
    string Command,
    long Created,
    string State,
    string Status,
    IReadOnlyList<PortMapping> Ports,
    IReadOnlyDictionary<string, string> Labels);

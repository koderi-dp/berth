namespace Berth.Engine.Models;

/// <summary>
/// A published port: <see cref="ContainerPort"/>/<see cref="Protocol"/> ("tcp" or "udp") on the host's <see cref="HostIp"/>
/// (null for every address) and <see cref="HostPort"/> (null for one the engine picks).
/// </summary>
public sealed record PortBinding(int ContainerPort, string Protocol, string? HostIp, int? HostPort);

/// <summary>
/// A container to create. <see cref="Name"/> null lets the engine name it. <see cref="Env"/> holds "KEY=value" entries and
/// <see cref="Binds"/> "source:destination[:options]" entries, as <c>docker run -e</c> and <c>-v</c> take them. <see cref="Cmd"/>
/// null keeps the image's command.
/// </summary>
public sealed record ContainerCreateRequest(
    string Image,
    string? Name,
    IReadOnlyList<string> Env,
    IReadOnlyList<PortBinding> Ports,
    IReadOnlyList<string> Binds,
    IReadOnlyList<string>? Cmd = null);

namespace Berth.Engine.Models;

/// <summary>
/// A container's state from inspect. <see cref="StartedAt"/> and <see cref="FinishedAt"/> are null when the container never
/// started or never finished; <see cref="Health"/> is null when the container has no health check.
/// </summary>
public sealed record ContainerStateDetails(
    string Status,
    bool Running,
    bool Paused,
    bool Restarting,
    int ExitCode,
    string Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Health);

/// <summary>A mount of a container; <see cref="Name"/> is the volume's name, null for a bind mount.</summary>
public sealed record MountDetails(string Type, string Source, string Destination, string Mode, bool ReadWrite, string? Name);

/// <summary>A network the container is attached to, with its address on that network.</summary>
public sealed record NetworkAttachment(string Name, string IpAddress, string Gateway, string MacAddress);

/// <summary>
/// The parts of a container's inspect answer Berth shows. <see cref="Name"/> has no leading "/". <see cref="ImageId"/> is the
/// image's ID and <see cref="Image"/> the name the container was created from. <see cref="Ports"/> lists each published binding,
/// and an exposed port that is not published once with a null address and public port.
/// </summary>
public sealed record ContainerDetails(
    string Id,
    string Name,
    DateTimeOffset Created,
    string Path,
    IReadOnlyList<string> Args,
    ContainerStateDetails State,
    string ImageId,
    string Image,
    IReadOnlyList<string> Env,
    IReadOnlyList<string> Cmd,
    IReadOnlyList<string> Entrypoint,
    string WorkingDir,
    string User,
    bool Tty,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<MountDetails> Mounts,
    IReadOnlyList<PortMapping> Ports,
    IReadOnlyList<NetworkAttachment> Networks,
    string RestartPolicy,
    string Platform)
{
    /// <summary>
    /// How many CPUs the container may use (<c>--cpus</c>, or <c>--cpu-quota</c> over <c>--cpu-period</c>), or null when it has no
    /// CPU limit.
    /// </summary>
    public double? CpuLimit { get; init; }
}

/// <summary>A container's inspect answer: the mapped <see cref="Details"/> and the engine's whole answer, indented.</summary>
public sealed record ContainerInspection(ContainerDetails Details, string RawJson);

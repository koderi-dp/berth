using System.Text.Json.Serialization;

namespace Berth.Engine;

/// <summary>
/// The source-generated serializer for engine answers. The wire records below mirror the engine's PascalCase JSON with every field
/// optional; <see cref="EngineClient"/> maps them to the public models so a missing or null field never reaches the UI as null.
/// </summary>
[JsonSerializable(typeof(WireVersion))]
[JsonSerializable(typeof(WireInfo))]
[JsonSerializable(typeof(List<WireContainer>))]
[JsonSerializable(typeof(WireEvent))]
[JsonSerializable(typeof(WireInspect))]
[JsonSerializable(typeof(WireStats))]
[JsonSerializable(typeof(WireError))]
[JsonSerializable(typeof(List<WireImage>))]
[JsonSerializable(typeof(WireImageInspect))]
[JsonSerializable(typeof(List<WireImageDelete>))]
[JsonSerializable(typeof(WireImagePrune))]
[JsonSerializable(typeof(WirePullLine))]
[JsonSerializable(typeof(WireVolumeList))]
[JsonSerializable(typeof(WireVolume))]
[JsonSerializable(typeof(WireVolumeCreate))]
[JsonSerializable(typeof(WireVolumePrune))]
[JsonSerializable(typeof(List<WireNetwork>))]
[JsonSerializable(typeof(WireNetwork))]
[JsonSerializable(typeof(WireNetworkPrune))]
[JsonSerializable(typeof(WireDiskUsage))]
[JsonSerializable(typeof(WireCreateContainer))]
[JsonSerializable(typeof(WireCreated))]
internal sealed partial class EngineJson : JsonSerializerContext;

internal sealed record WireVersion(
    [property: JsonPropertyName("Version")] string? Version,
    [property: JsonPropertyName("ApiVersion")] string? ApiVersion,
    [property: JsonPropertyName("MinAPIVersion")] string? MinApiVersion,
    [property: JsonPropertyName("Os")] string? Os,
    [property: JsonPropertyName("Arch")] string? Arch);

internal sealed record WireInfo(
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("ServerVersion")] string? ServerVersion,
    [property: JsonPropertyName("NCPU")] int Cpus,
    [property: JsonPropertyName("MemTotal")] long MemoryBytes,
    [property: JsonPropertyName("ContainersRunning")] int ContainersRunning,
    [property: JsonPropertyName("ContainersPaused")] int ContainersPaused,
    [property: JsonPropertyName("ContainersStopped")] int ContainersStopped,
    [property: JsonPropertyName("Images")] int Images);

internal sealed record WirePort(
    [property: JsonPropertyName("IP")] string? Ip,
    [property: JsonPropertyName("PrivatePort")] int PrivatePort,
    [property: JsonPropertyName("PublicPort")] int? PublicPort,
    [property: JsonPropertyName("Type")] string? Type);

internal sealed record WireContainer(
    [property: JsonPropertyName("Id")] string? Id,
    [property: JsonPropertyName("Names")] List<string>? Names,
    [property: JsonPropertyName("Image")] string? Image,
    [property: JsonPropertyName("ImageID")] string? ImageId,
    [property: JsonPropertyName("Command")] string? Command,
    [property: JsonPropertyName("Created")] long Created,
    [property: JsonPropertyName("State")] string? State,
    [property: JsonPropertyName("Status")] string? Status,
    [property: JsonPropertyName("Ports")] List<WirePort>? Ports,
    [property: JsonPropertyName("Labels")] Dictionary<string, string>? Labels,
    [property: JsonPropertyName("Mounts")] List<WireMount>? Mounts,
    [property: JsonPropertyName("NetworkSettings")] WireNetworkSettings? NetworkSettings);

internal sealed record WireActor(
    [property: JsonPropertyName("ID")] string? Id,
    [property: JsonPropertyName("Attributes")] Dictionary<string, string>? Attributes);

/// <summary>One line of the event stream.</summary>
internal sealed record WireEvent(
    [property: JsonPropertyName("Type")] string? Type,
    [property: JsonPropertyName("Action")] string? Action,
    [property: JsonPropertyName("Actor")] WireActor? Actor,
    [property: JsonPropertyName("timeNano")] long TimeNano);

/// <summary>A container's inspect answer (<c>/containers/{id}/json</c>), the parts Berth maps.</summary>
internal sealed record WireInspect(
    [property: JsonPropertyName("Id")] string? Id,
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("Created")] string? Created,
    [property: JsonPropertyName("Path")] string? Path,
    [property: JsonPropertyName("Args")] List<string>? Args,
    [property: JsonPropertyName("State")] WireState? State,
    [property: JsonPropertyName("Image")] string? Image,
    [property: JsonPropertyName("Platform")] string? Platform,
    [property: JsonPropertyName("Config")] WireConfig? Config,
    [property: JsonPropertyName("HostConfig")] WireHostConfig? HostConfig,
    [property: JsonPropertyName("Mounts")] List<WireMount>? Mounts,
    [property: JsonPropertyName("NetworkSettings")] WireNetworkSettings? NetworkSettings);

internal sealed record WireState(
    [property: JsonPropertyName("Status")] string? Status,
    [property: JsonPropertyName("Running")] bool Running,
    [property: JsonPropertyName("Paused")] bool Paused,
    [property: JsonPropertyName("Restarting")] bool Restarting,
    [property: JsonPropertyName("ExitCode")] int ExitCode,
    [property: JsonPropertyName("Error")] string? Error,
    [property: JsonPropertyName("StartedAt")] string? StartedAt,
    [property: JsonPropertyName("FinishedAt")] string? FinishedAt,
    [property: JsonPropertyName("Health")] WireHealth? Health);

internal sealed record WireHealth([property: JsonPropertyName("Status")] string? Status);

/// <summary>A container's or an image's configuration; <c>ExposedPorts</c> maps "80/tcp" to an empty object.</summary>
internal sealed record WireConfig(
    [property: JsonPropertyName("Image")] string? Image,
    [property: JsonPropertyName("Env")] List<string>? Env,
    [property: JsonPropertyName("Cmd")] List<string>? Cmd,
    [property: JsonPropertyName("Entrypoint")] List<string>? Entrypoint,
    [property: JsonPropertyName("WorkingDir")] string? WorkingDir,
    [property: JsonPropertyName("User")] string? User,
    [property: JsonPropertyName("Tty")] bool Tty,
    [property: JsonPropertyName("Labels")] Dictionary<string, string>? Labels,
    [property: JsonPropertyName("ExposedPorts")] Dictionary<string, WireEmpty?>? ExposedPorts);

internal sealed record WireHostConfig(
    [property: JsonPropertyName("RestartPolicy")] WireRestartPolicy? RestartPolicy,
    [property: JsonPropertyName("NanoCpus")] long? NanoCpus,
    [property: JsonPropertyName("CpuQuota")] long? CpuQuota,
    [property: JsonPropertyName("CpuPeriod")] long? CpuPeriod);

internal sealed record WireRestartPolicy([property: JsonPropertyName("Name")] string? Name);

internal sealed record WireMount(
    [property: JsonPropertyName("Type")] string? Type,
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("Source")] string? Source,
    [property: JsonPropertyName("Destination")] string? Destination,
    [property: JsonPropertyName("Mode")] string? Mode,
    [property: JsonPropertyName("RW")] bool ReadWrite);

/// <summary>
/// <c>Ports</c> maps "80/tcp" to its host bindings, null or empty when the port is exposed but not published (inspect only). The
/// keys of <c>Networks</c> are the names of the networks the container is attached to.
/// </summary>
internal sealed record WireNetworkSettings(
    [property: JsonPropertyName("Ports")] Dictionary<string, List<WirePortBinding>?>? Ports,
    [property: JsonPropertyName("Networks")] Dictionary<string, WireEndpoint?>? Networks);

internal sealed record WirePortBinding(
    [property: JsonPropertyName("HostIp")] string? HostIp,
    [property: JsonPropertyName("HostPort")] string? HostPort);

internal sealed record WireEndpoint(
    [property: JsonPropertyName("IPAddress")] string? IpAddress,
    [property: JsonPropertyName("Gateway")] string? Gateway,
    [property: JsonPropertyName("MacAddress")] string? MacAddress);

/// <summary>One line of the stats stream (<c>/containers/{id}/stats</c>). The engine's counters are unsigned 64-bit numbers.</summary>
internal sealed record WireStats(
    [property: JsonPropertyName("read")] string? Read,
    [property: JsonPropertyName("cpu_stats")] WireCpuStats? CpuStats,
    [property: JsonPropertyName("precpu_stats")] WireCpuStats? PreCpuStats,
    [property: JsonPropertyName("memory_stats")] WireMemoryStats? MemoryStats,
    [property: JsonPropertyName("networks")] Dictionary<string, WireNetworkStats?>? Networks,
    [property: JsonPropertyName("blkio_stats")] WireBlockIoStats? BlockIoStats,
    [property: JsonPropertyName("pids_stats")] WirePidsStats? PidsStats);

internal sealed record WireCpuStats(
    [property: JsonPropertyName("cpu_usage")] WireCpuUsage? CpuUsage,
    [property: JsonPropertyName("system_cpu_usage")] ulong? SystemCpuUsage,
    [property: JsonPropertyName("online_cpus")] uint? OnlineCpus);

internal sealed record WireCpuUsage(
    [property: JsonPropertyName("total_usage")] ulong? TotalUsage,
    [property: JsonPropertyName("percpu_usage")] List<ulong>? PerCpuUsage);

/// <summary><c>stats</c> holds the cgroup's memory figures: <c>inactive_file</c> on cgroup v2, <c>total_inactive_file</c> on v1.</summary>
internal sealed record WireMemoryStats(
    [property: JsonPropertyName("usage")] ulong? Usage,
    [property: JsonPropertyName("limit")] ulong? Limit,
    [property: JsonPropertyName("stats")] Dictionary<string, ulong>? Stats);

internal sealed record WireNetworkStats(
    [property: JsonPropertyName("rx_bytes")] ulong? ReceivedBytes,
    [property: JsonPropertyName("tx_bytes")] ulong? SentBytes);

internal sealed record WireBlockIoStats(
    [property: JsonPropertyName("io_service_bytes_recursive")] List<WireBlockIoEntry?>? ServiceBytes);

internal sealed record WireBlockIoEntry(
    [property: JsonPropertyName("op")] string? Op,
    [property: JsonPropertyName("value")] ulong? Value);

internal sealed record WirePidsStats([property: JsonPropertyName("current")] ulong? Current);

/// <summary>The body the engine sends with an error status, and the <c>errorDetail</c> of a pull's error line.</summary>
internal sealed record WireError([property: JsonPropertyName("message")] string? Message);

/// <summary>An empty JSON object, as the values of <c>ExposedPorts</c>.</summary>
internal sealed record WireEmpty;

/// <summary>
/// One entry of <c>/images/json</c> and of the images of <c>/system/df</c>. <c>Containers</c> and <c>SharedSize</c> are -1 when
/// the engine did not compute them.
/// </summary>
internal sealed record WireImage(
    [property: JsonPropertyName("Id")] string? Id,
    [property: JsonPropertyName("RepoTags")] List<string>? RepoTags,
    [property: JsonPropertyName("RepoDigests")] List<string>? RepoDigests,
    [property: JsonPropertyName("Created")] long Created,
    [property: JsonPropertyName("Size")] long Size,
    [property: JsonPropertyName("SharedSize")] long? SharedSize,
    [property: JsonPropertyName("Containers")] int? Containers);

/// <summary>An image's inspect answer (<c>/images/{name}/json</c>), the parts Berth maps.</summary>
internal sealed record WireImageInspect(
    [property: JsonPropertyName("Id")] string? Id,
    [property: JsonPropertyName("RepoTags")] List<string>? RepoTags,
    [property: JsonPropertyName("RepoDigests")] List<string>? RepoDigests,
    [property: JsonPropertyName("Created")] string? Created,
    [property: JsonPropertyName("Size")] long Size,
    [property: JsonPropertyName("Architecture")] string? Architecture,
    [property: JsonPropertyName("Os")] string? Os,
    [property: JsonPropertyName("Config")] WireConfig? Config);

/// <summary>One line of an image removal's answer, and one entry of an image prune's.</summary>
internal sealed record WireImageDelete(
    [property: JsonPropertyName("Untagged")] string? Untagged,
    [property: JsonPropertyName("Deleted")] string? Deleted);

internal sealed record WireImagePrune(
    [property: JsonPropertyName("ImagesDeleted")] List<WireImageDelete>? ImagesDeleted,
    [property: JsonPropertyName("SpaceReclaimed")] long SpaceReclaimed);

/// <summary>One line of a pull (<c>/images/create</c>): progress, or a failure in <c>error</c> and <c>errorDetail</c>.</summary>
internal sealed record WirePullLine(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("progressDetail")] WireProgressDetail? ProgressDetail,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("errorDetail")] WireError? ErrorDetail);

internal sealed record WireProgressDetail(
    [property: JsonPropertyName("current")] long? Current,
    [property: JsonPropertyName("total")] long? Total);

internal sealed record WireVolumeList([property: JsonPropertyName("Volumes")] List<WireVolume>? Volumes);

/// <summary>A volume, from <c>/volumes</c> or <c>/system/df</c> (which adds <c>UsageData</c>).</summary>
internal sealed record WireVolume(
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("Driver")] string? Driver,
    [property: JsonPropertyName("Mountpoint")] string? Mountpoint,
    [property: JsonPropertyName("CreatedAt")] string? CreatedAt,
    [property: JsonPropertyName("Labels")] Dictionary<string, string>? Labels,
    [property: JsonPropertyName("Scope")] string? Scope,
    [property: JsonPropertyName("UsageData")] WireVolumeUsage? UsageData);

/// <summary>A volume's size and how many containers use it; either is -1 when the engine could not tell.</summary>
internal sealed record WireVolumeUsage(
    [property: JsonPropertyName("Size")] long Size,
    [property: JsonPropertyName("RefCount")] int RefCount);

/// <summary>The body of <c>/volumes/create</c>.</summary>
internal sealed record WireVolumeCreate([property: JsonPropertyName("Name")] string Name);

internal sealed record WireVolumePrune(
    [property: JsonPropertyName("VolumesDeleted")] List<string>? VolumesDeleted,
    [property: JsonPropertyName("SpaceReclaimed")] long SpaceReclaimed);

/// <summary>A network, from <c>/networks</c> or its inspect answer (which adds <c>Containers</c>, keyed by container ID).</summary>
internal sealed record WireNetwork(
    [property: JsonPropertyName("Id")] string? Id,
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("Driver")] string? Driver,
    [property: JsonPropertyName("Scope")] string? Scope,
    [property: JsonPropertyName("Created")] string? Created,
    [property: JsonPropertyName("Internal")] bool Internal,
    [property: JsonPropertyName("Labels")] Dictionary<string, string>? Labels,
    [property: JsonPropertyName("IPAM")] WireIpam? Ipam,
    [property: JsonPropertyName("Containers")] Dictionary<string, WireNetworkContainer?>? Containers);

internal sealed record WireIpam([property: JsonPropertyName("Config")] List<WireIpamConfig?>? Config);

internal sealed record WireIpamConfig([property: JsonPropertyName("Subnet")] string? Subnet);

internal sealed record WireNetworkContainer(
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("MacAddress")] string? MacAddress,
    [property: JsonPropertyName("IPv4Address")] string? IPv4Address,
    [property: JsonPropertyName("IPv6Address")] string? IPv6Address);

internal sealed record WireNetworkPrune([property: JsonPropertyName("NetworksDeleted")] List<string>? NetworksDeleted);

/// <summary>
/// <c>/system/df</c>. Engines before API 1.52 send only <c>LayersSize</c> and the lists; later ones add their own totals with the
/// same items (and leave the lists out when asked for <c>verbose</c>).
/// </summary>
internal sealed record WireDiskUsage(
    [property: JsonPropertyName("LayersSize")] long LayersSize,
    [property: JsonPropertyName("Images")] List<WireImage>? Images,
    [property: JsonPropertyName("Volumes")] List<WireVolume>? Volumes,
    [property: JsonPropertyName("ImageUsage")] WireUsage<WireImage>? ImageUsage,
    [property: JsonPropertyName("VolumeUsage")] WireUsage<WireVolume>? VolumeUsage);

internal sealed record WireUsage<T>(
    [property: JsonPropertyName("TotalCount")] int TotalCount,
    [property: JsonPropertyName("ActiveCount")] int ActiveCount,
    [property: JsonPropertyName("TotalSize")] long TotalSize,
    [property: JsonPropertyName("Reclaimable")] long Reclaimable,
    [property: JsonPropertyName("Items")] List<T>? Items);

/// <summary>The body of <c>/containers/create</c>. <c>Cmd</c> is left out when null, so the image's command applies.</summary>
internal sealed record WireCreateContainer(
    [property: JsonPropertyName("Image")] string Image,
    [property: JsonPropertyName("Env")] IReadOnlyList<string> Env,
    [property: JsonPropertyName("Cmd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Cmd,
    [property: JsonPropertyName("ExposedPorts")] Dictionary<string, WireEmpty> ExposedPorts,
    [property: JsonPropertyName("HostConfig")] WireCreateHostConfig HostConfig);

internal sealed record WireCreateHostConfig(
    [property: JsonPropertyName("Binds")] IReadOnlyList<string> Binds,
    [property: JsonPropertyName("PortBindings")] Dictionary<string, List<WirePortBinding>> PortBindings);

internal sealed record WireCreated([property: JsonPropertyName("Id")] string? Id);

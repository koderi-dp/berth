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
[JsonSerializable(typeof(WireError))]
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
    [property: JsonPropertyName("Labels")] Dictionary<string, string>? Labels);

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

internal sealed record WireConfig(
    [property: JsonPropertyName("Image")] string? Image,
    [property: JsonPropertyName("Env")] List<string>? Env,
    [property: JsonPropertyName("Cmd")] List<string>? Cmd,
    [property: JsonPropertyName("Entrypoint")] List<string>? Entrypoint,
    [property: JsonPropertyName("WorkingDir")] string? WorkingDir,
    [property: JsonPropertyName("User")] string? User,
    [property: JsonPropertyName("Tty")] bool Tty,
    [property: JsonPropertyName("Labels")] Dictionary<string, string>? Labels);

internal sealed record WireHostConfig([property: JsonPropertyName("RestartPolicy")] WireRestartPolicy? RestartPolicy);

internal sealed record WireRestartPolicy([property: JsonPropertyName("Name")] string? Name);

internal sealed record WireMount(
    [property: JsonPropertyName("Type")] string? Type,
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("Source")] string? Source,
    [property: JsonPropertyName("Destination")] string? Destination,
    [property: JsonPropertyName("Mode")] string? Mode,
    [property: JsonPropertyName("RW")] bool ReadWrite);

/// <summary><c>Ports</c> maps "80/tcp" to its host bindings, null or empty when the port is exposed but not published.</summary>
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

/// <summary>The body the engine sends with an error status.</summary>
internal sealed record WireError([property: JsonPropertyName("message")] string? Message);

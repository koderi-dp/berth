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

/// <summary>The body the engine sends with an error status.</summary>
internal sealed record WireError([property: JsonPropertyName("message")] string? Message);

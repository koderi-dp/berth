using System.Text.Json.Serialization;

namespace Berth.Settings;

/// <summary>
/// The source-generated serializer for <c>settings.json</c>: <see cref="AppSettings"/> to write it, <see cref="SettingsFile"/>
/// (every field optional, unknown ones ignored) to read it. JSON that does not have the right shape, such as a string where a
/// number belongs or an unknown kind name, makes the file unreadable as settings, the same as a file that is not JSON.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SettingsFile))]
internal sealed partial class SettingsJson : JsonSerializerContext;

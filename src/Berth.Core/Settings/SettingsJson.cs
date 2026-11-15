using System.Text.Json.Serialization;

namespace Berth.Settings;

/// <summary>
/// The source-generated serializer for <c>settings.json</c>. A field the record requires that is missing or null makes the file
/// unreadable as settings, the same as a file that is not JSON.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

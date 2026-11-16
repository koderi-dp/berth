namespace Berth.Engine.Models;

/// <summary>One volume. <see cref="CreatedAt"/> is null when the engine sent no readable time.</summary>
public sealed record VolumeSummary(
    string Name,
    string Driver,
    string Mountpoint,
    DateTimeOffset? CreatedAt,
    IReadOnlyDictionary<string, string> Labels,
    string Scope);

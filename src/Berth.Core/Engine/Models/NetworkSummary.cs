namespace Berth.Engine.Models;

/// <summary>
/// One network. <see cref="Created"/> is null when the engine sent no readable time. <see cref="Subnets"/> are the IPAM
/// configuration's subnets ("172.18.0.0/16"), empty when it has none.
/// </summary>
public sealed record NetworkSummary(
    string Id,
    string Name,
    string Driver,
    string Scope,
    DateTimeOffset? Created,
    bool Internal,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> Subnets);


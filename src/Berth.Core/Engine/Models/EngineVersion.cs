namespace Berth.Engine.Models;

/// <summary>What the engine reports at <c>/version</c>.</summary>
public sealed record EngineVersion(string Version, string ApiVersion, string MinApiVersion, string Os, string Arch);

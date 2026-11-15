namespace Berth.Engine.Models;

/// <summary>One message from the engine's event stream.</summary>
public sealed record EngineEventMessage(string Type, string Action, string ActorId, IReadOnlyDictionary<string, string> Attributes, long TimeNano);

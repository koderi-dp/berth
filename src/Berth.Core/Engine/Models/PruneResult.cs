namespace Berth.Engine.Models;

/// <summary>
/// What a prune removed: image IDs, volume names or network names, and the bytes it freed (always 0 for networks).
/// </summary>
public sealed record PruneResult(IReadOnlyList<string> Deleted, long SpaceReclaimed);

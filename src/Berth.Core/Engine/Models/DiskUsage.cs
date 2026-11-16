namespace Berth.Engine.Models;

/// <summary>
/// An image's disk use. <see cref="SharedSize"/> is what it shares with other images and <see cref="UniqueSize"/> what only it
/// holds; both are null when the engine did not compute the shared size.
/// </summary>
public sealed record ImageDiskUsage(string Id, long Size, long? SharedSize, long? UniqueSize, int Containers);

/// <summary>A volume's disk use; <see cref="Size"/> is null when the engine could not measure it (a driver other than local).</summary>
public sealed record VolumeDiskUsage(string Name, long? Size, int RefCount);

/// <summary>Totals over one kind: how many, how many in use, their size, and what removing the unused ones would free.</summary>
public sealed record UsageTotals(int TotalCount, int ActiveCount, long TotalSize, long Reclaimable);

/// <summary>The engine's disk use (<c>/system/df</c>) for images and volumes.</summary>
public sealed record DiskUsage(
    IReadOnlyList<ImageDiskUsage> Images,
    UsageTotals ImageTotals,
    IReadOnlyList<VolumeDiskUsage> Volumes,
    UsageTotals VolumeTotals);

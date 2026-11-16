using Berth.Engine.Models;

namespace Berth.Engine;

/// <summary>
/// Maps <c>/system/df</c> to <see cref="DiskUsage"/>. An engine from API 1.52 on sends its own totals (<c>ImageUsage</c>,
/// <c>VolumeUsage</c>) with their items; an older one sends only the lists, and the totals are computed as the docker CLI does.
/// </summary>
internal static class DiskUsageMapping
{
    public static DiskUsage From(WireDiskUsage wire)
    {
        var images = (wire.ImageUsage?.Items ?? wire.Images ?? []).ConvertAll(ToImage);
        var volumes = (wire.VolumeUsage?.Items ?? wire.Volumes ?? []).ConvertAll(ToVolume);
        return new DiskUsage(
            images,
            wire.ImageUsage is { } imageUsage ? Totals(imageUsage) : ImageTotals(images, wire.LayersSize),
            volumes,
            wire.VolumeUsage is { } volumeUsage ? Totals(volumeUsage) : VolumeTotals(volumes));
    }

    private static ImageDiskUsage ToImage(WireImage i)
    {
        long? shared = i.SharedSize is >= 0 ? i.SharedSize : null;
        return new ImageDiskUsage(i.Id ?? "", i.Size, shared, i.Size - shared, Math.Max(0, i.Containers ?? 0));
    }

    private static VolumeDiskUsage ToVolume(WireVolume v) =>
        new(v.Name ?? "", v.UsageData?.Size is >= 0 ? v.UsageData.Size : null, Math.Max(0, v.UsageData?.RefCount ?? 0));

    private static UsageTotals Totals<T>(WireUsage<T> usage) =>
        new(usage.TotalCount, usage.ActiveCount, usage.TotalSize, usage.Reclaimable);

    /// <summary>
    /// The layers' size is the total; what images in use hold alone (their size less what they share) is kept, the rest could be
    /// reclaimed. An image whose shared size was not computed counts for nothing, as in the docker CLI.
    /// </summary>
    private static UsageTotals ImageTotals(List<ImageDiskUsage> images, long layersSize)
    {
        var inUse = images.Where(i => i.Containers > 0).ToList();
        var kept = inUse.Sum(i => i.UniqueSize ?? 0);
        return new UsageTotals(images.Count, inUse.Count, layersSize, Math.Max(0, layersSize - kept));
    }

    /// <summary>Volumes whose size the engine could not measure count for nothing.</summary>
    private static UsageTotals VolumeTotals(List<VolumeDiskUsage> volumes) => new(
        volumes.Count,
        volumes.Count(v => v.RefCount > 0),
        volumes.Sum(v => v.Size ?? 0),
        volumes.Where(v => v.RefCount == 0).Sum(v => v.Size ?? 0));
}

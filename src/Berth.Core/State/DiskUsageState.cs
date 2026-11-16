using Berth.Engine.Models;

namespace Berth.State;

/// <summary>
/// The engine's disk use of images and volumes, read only when asked for (it can be slow) and again after a remove or prune once it
/// has been asked for.
/// </summary>
/// <param name="Usage">The last disk use read; null until one arrives. Kept while a new read runs.</param>
/// <param name="Asked">Something has asked for it in this session.</param>
/// <param name="Loading">A read is running.</param>
/// <param name="Generation">The latest read; results of older reads are dropped.</param>
public sealed record DiskUsageState(DiskUsage? Usage, bool Asked, bool Loading, int Generation)
{
    public static readonly DiskUsageState Initial = new(Usage: null, Asked: false, Loading: false, Generation: 0);
}

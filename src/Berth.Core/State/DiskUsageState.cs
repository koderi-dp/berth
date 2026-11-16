using Berth.Engine.Models;

namespace Berth.State;

/// <summary>
/// The engine's disk use of images and volumes, read only when asked for (it can be slow) and again after a batch of removes once it
/// has been asked for.
/// </summary>
/// <param name="Usage">The last disk use read; null until one arrives. Kept while a new read runs.</param>
/// <param name="Asked">Something has asked for it in this session.</param>
/// <param name="Loading">A read is running.</param>
/// <param name="Generation">The latest read; results of older reads are dropped.</param>
/// <param name="Again">Asked for again while a read ran: one more read starts when it ends, however often it was asked.</param>
public sealed record DiskUsageState(DiskUsage? Usage, bool Asked, bool Loading, int Generation, bool Again = false)
{
    public static readonly DiskUsageState Initial = new(Usage: null, Asked: false, Loading: false, Generation: 0);
}

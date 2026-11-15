using Berth.Engine.Models;

namespace Berth.Engine;

/// <summary>Turns one line of the engine's stats stream into the figures Berth shows, the way the docker CLI computes them.</summary>
internal static class StatsMath
{
    /// <summary>The sample for <paramref name="stats"/>; a missing section counts as 0.</summary>
    public static StatsSample ToSample(WireStats stats)
    {
        var memory = stats.MemoryStats;
        return new StatsSample(
            EngineTime.Parse(stats.Read) ?? DateTimeOffset.MinValue,
            CpuPercent(stats.CpuStats, stats.PreCpuStats),
            ToLong(MemoryUsed(memory)),
            ToLong(memory?.Limit ?? 0),
            ToLong(Sum(stats.Networks?.Values.Select(n => n?.ReceivedBytes ?? 0))),
            ToLong(Sum(stats.Networks?.Values.Select(n => n?.SentBytes ?? 0))),
            ToLong(BlockIo(stats.BlockIoStats, "read")),
            ToLong(BlockIo(stats.BlockIoStats, "write")),
            (int)Math.Min(stats.PidsStats?.Current ?? 0, int.MaxValue));
    }

    /// <summary>
    /// <c>(cpu delta / system delta) * online CPUs * 100</c>, the deltas taken against the previous reading. 0 when there is no
    /// previous reading, when the system delta is not positive or when the CPU delta is negative. The CPU count falls back to
    /// the length of the per-CPU list, then to 1.
    /// </summary>
    private static double CpuPercent(WireCpuStats? now, WireCpuStats? before)
    {
        var previousSystem = before?.SystemCpuUsage ?? 0;
        var system = now?.SystemCpuUsage ?? 0;
        var previousCpu = before?.CpuUsage?.TotalUsage ?? 0;
        var cpu = now?.CpuUsage?.TotalUsage ?? 0;
        if (previousSystem == 0 || system <= previousSystem || cpu < previousCpu)
        {
            return 0;
        }

        double cpus = now?.OnlineCpus is > 0 and var online ? online
            : now?.CpuUsage?.PerCpuUsage is { Count: > 0 } perCpu ? perCpu.Count
            : 1;
        return (double)(cpu - previousCpu) / (system - previousSystem) * cpus * 100;
    }

    /// <summary>
    /// Usage less inactive file pages, never below 0: v1's hierarchical <c>total_inactive_file</c> when present (v1 also carries
    /// a per-cgroup <c>inactive_file</c>), else v2's <c>inactive_file</c>.
    /// </summary>
    private static ulong MemoryUsed(WireMemoryStats? memory)
    {
        var usage = memory?.Usage ?? 0;
        var figures = memory?.Stats;
        if (figures is null)
        {
            return usage;
        }

        var inactive = figures.TryGetValue("total_inactive_file", out var v1) ? v1
            : figures.TryGetValue("inactive_file", out var v2) ? v2
            : 0;
        return usage > inactive ? usage - inactive : 0;
    }

    private static ulong BlockIo(WireBlockIoStats? blockIo, string op) =>
        Sum(blockIo?.ServiceBytes?
            .Where(e => string.Equals(e?.Op, op, StringComparison.OrdinalIgnoreCase))
            .Select(e => e!.Value ?? 0));

    private static ulong Sum(IEnumerable<ulong>? values)
    {
        ulong total = 0;
        foreach (var value in values ?? [])
        {
            total = value > ulong.MaxValue - total ? ulong.MaxValue : total + value;
        }

        return total;
    }

    private static long ToLong(ulong value) => (long)Math.Min(value, long.MaxValue);
}

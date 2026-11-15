using Berth.Session;

namespace Berth.Presentation;

/// <summary>
/// What the containers list's summary shows: CPU in percent of one CPU and memory in bytes, summed over the running
/// containers, next to the engine's CPU count and memory (0 before the engine's info is read).
/// </summary>
public sealed record StatsTotals(double CpuPercent, int Cpus, long MemoryBytes, long MemoryLimitBytes);

public static class StatsSummary
{
    /// <summary>Sums the latest stats of the containers <paramref name="state"/> lists as running.</summary>
    public static StatsTotals Totals(EngineState state)
    {
        double cpu = 0;
        long memory = 0;
        foreach (var container in state.Containers)
        {
            if (container.State == "running" && state.Stats.TryGetValue(container.Id, out var sample))
            {
                cpu += sample.CpuPercent;
                memory += sample.MemoryBytes;
            }
        }

        return new StatsTotals(cpu, state.Info?.Cpus ?? 0, memory, state.Info?.MemoryBytes ?? 0);
    }
}

using Berth.Engine.Models;
using Berth.Session;

namespace Berth.Presentation;

/// <summary>
/// What the containers list's summary shows: CPU in percent of one CPU and memory in bytes, summed over the running
/// containers, next to the engine's CPU count and memory (0 before the engine's info is read).
/// </summary>
public sealed record StatsTotals(double CpuPercent, int Cpus, long MemoryBytes, long MemoryLimitBytes)
{
    /// <summary>
    /// The sums are known: a running container has a sample, or none runs. False until the first batch of a watch arrives (turning
    /// list stats off clears them), when the sums would read 0 for what is not known yet.
    /// </summary>
    public bool Known { get; init; } = true;
}

public static class StatsSummary
{
    /// <summary>Sums the latest stats of the containers <paramref name="state"/> lists as running.</summary>
    public static StatsTotals Totals(EngineState state)
    {
        double cpu = 0;
        long memory = 0;
        var running = false;
        var sampled = false;
        foreach (var container in state.Containers)
        {
            if (container.State != "running")
                continue;
            running = true;
            if (state.Stats.TryGetValue(container.Id, out var sample))
            {
                sampled = true;
                cpu += sample.CpuPercent;
                memory += sample.MemoryBytes;
            }
        }

        return new StatsTotals(cpu, state.Info?.Cpus ?? 0, memory, state.Info?.MemoryBytes ?? 0) { Known = !running || sampled };
    }

    /// <summary>
    /// The list's CPU figure: "1.4% / 800% (8 CPUs)", out of every CPU of the engine (100% each); just "1.4%" before the engine's
    /// info is read.
    /// </summary>
    public static string CpuText(StatsTotals totals) =>
        totals.Cpus > 0
            ? $"{CpuFigure(totals)} / {totals.Cpus * 100}% ({totals.Cpus} {(totals.Cpus == 1 ? "CPU" : "CPUs")})"
            : CpuFigure(totals);

    /// <summary>The list's memory figure: "988 MB / 7.47 GB", out of the engine's memory; just "988 MB" before its info is read.</summary>
    public static string MemoryText(StatsTotals totals) =>
        totals.MemoryLimitBytes > 0
            ? $"{MemoryFigure(totals)} / {ByteFormat.Bytes(totals.MemoryLimitBytes)}"
            : MemoryFigure(totals);

    /// <summary>What stands for a figure not known yet (<see cref="StatsTotals.Known"/>).</summary>
    public const string Unknown = "—";

    private static string CpuFigure(StatsTotals totals) => totals.Known ? Percent(totals.CpuPercent) : Unknown;

    private static string MemoryFigure(StatsTotals totals) => totals.Known ? ByteFormat.Bytes(totals.MemoryBytes) : Unknown;

    /// <summary>A row's CPU in percent of one CPU, or null when it is not running or has no sample yet.</summary>
    public static double? CpuOf(ContainerRow row, IReadOnlyDictionary<string, StatsSample>? stats) =>
        row.State == RowState.Running && stats is not null && stats.TryGetValue(row.Id, out var sample) ? sample.CpuPercent : null;

    /// <summary>A row's CPU column: "12.4%", or empty when it has no figure (<see cref="CpuOf"/>).</summary>
    public static string CpuCell(ContainerRow row, IReadOnlyDictionary<string, StatsSample>? stats) =>
        CpuOf(row, stats) is { } cpu ? Percent(cpu) : "";

    /// <summary>A CPU percentage with one decimal: "12.4%".</summary>
    public static string Percent(double value) =>
        (double.IsNaN(value) || value < 0 ? 0 : value).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
}

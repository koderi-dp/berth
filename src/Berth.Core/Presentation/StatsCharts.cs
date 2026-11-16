using System.Globalization;
using Berth.Engine.Models;
using Berth.Session;
using Berth.State;

namespace Berth.Presentation;

/// <summary>What the detail page's Stats tab shows in place of its charts (Waiting: for the engine), or that it shows them.</summary>
public enum StatsView { Loading, NotRunning, Failed, Charts, Waiting }

/// <summary>
/// A chart's y-axis: from 0 to <see cref="Max"/>, in units of <see cref="UnitSize"/> (1 for a percentage, 1024 for KB, …)
/// named <see cref="Unit"/>. <see cref="Max"/> is a round number of units, so the axis ends on a tick.
/// </summary>
public sealed record ChartScale(double Max, double UnitSize, string Unit)
{
    /// <summary><paramref name="value"/> (a percentage or bytes) in this axis's units.</summary>
    public double ToUnits(double value) => value / UnitSize;

    /// <summary>A tick of the axis, in units: "50%", "0 B/s", "1.5 MB".</summary>
    public string TickLabel(double units)
    {
        var number = Math.Round(units, 2).ToString("0.##", CultureInfo.InvariantCulture);
        return Unit == "%" ? number + "%" : number + " " + Unit;
    }
}

/// <summary>The Stats tab's decisions: which view it shows, each chart's axis, title figures and the text read to a screen reader.</summary>
public static class StatsCharts
{
    /// <summary>How far back the charts go, in seconds.</summary>
    public const double WindowSeconds = 120;

    /// <summary>About how many steps a y-axis has; the axis ends on a round number for this count.</summary>
    public const int TickCount = 5;

    public const string LoadingText = "Getting container stats…";

    public const string NotRunningText = "Start the container to see its stats.";

    public const string WaitingText = DetailPage.WaitingText;

    private const string Window = "in the last 2 minutes";

    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// While <paramref name="waiting"/> for the engine (see <see cref="DetailPage.Waiting"/>): the charts kept, or the waiting text
    /// when there are none; never the error with a Retry that cannot work, nor that the container stopped. Otherwise: Failed, the
    /// error with Retry; not following (Idle: the container was not running; Ended: it stopped), the note to start it; following,
    /// the charts once the first sample is in, the loading text until then.
    /// </summary>
    public static StatsView View(StreamStatus status, bool hasSample, bool waiting) =>
        waiting ? (hasSample ? StatsView.Charts : StatsView.Waiting)
        : status switch
        {
            StreamStatus.Failed => StatsView.Failed,
            StreamStatus.Idle or StreamStatus.Ended => StatsView.NotRunning,
            _ => hasSample ? StatsView.Charts : StatsView.Loading,
        };

    /// <summary>Where a point taken at <paramref name="time"/> sits on the time axis: seconds before the latest sample, 0 to the window.</summary>
    public static double SecondsAgo(DateTimeOffset latest, DateTimeOffset time) =>
        Math.Clamp((latest - time).TotalSeconds, 0, WindowSeconds);

    /// <summary>A tick of the time axis: "now" for the latest sample, else "60 s" (seconds before it).</summary>
    public static string TimeTick(double secondsAgo) =>
        secondsAgo <= 0 ? "now" : Math.Round(secondsAgo).ToString(CultureInfo.InvariantCulture) + " s";

    /// <summary>
    /// The CPU axis, in percent of one CPU: up to the container's CPU limit (<paramref name="cpuLimit"/> CPUs), or one CPU (100%)
    /// when it has none, or the highest value shown when that is more; rounded up to a tick.
    /// </summary>
    public static ChartScale Cpu(IReadOnlyList<StatsPoint> cpu, double? cpuLimit)
    {
        var floor = cpuLimit is > 0 and var limit ? limit * 100 : 100;
        return new ChartScale(Nice(Math.Max(floor, Highest(cpu))), 1, "%");
    }

    /// <summary>The container's memory limit when it has one below the machine's memory, so it is worth drawing; else null.</summary>
    public static long? MemoryLimit(StatsSample? latest, long machineMemoryBytes) =>
        latest is { MemoryLimitBytes: > 0 and var limit } && machineMemoryBytes > 0 && limit < machineMemoryBytes ? limit : null;

    /// <summary>The memory axis: up to the highest value shown and the drawn <paramref name="limit"/>, at least 1 MB.</summary>
    public static ChartScale Memory(IReadOnlyList<StatsPoint> memory, long? limit) =>
        Bytes(Math.Max(Math.Max(Highest(memory), limit ?? 0), 1 << 20), "");

    /// <summary>A rate axis in bytes per second: up to the highest value of both series, at least 1 KB/s.</summary>
    public static ChartScale Rate(IReadOnlyList<StatsPoint> first, IReadOnlyList<StatsPoint> second) =>
        Bytes(Math.Max(Math.Max(Highest(first), Highest(second)), 1024), "/s");

    /// <summary>The CPU chart's title figure: "12.4%".</summary>
    public static string CpuValue(StatsSample latest) => StatsSummary.Percent(latest.CpuPercent);

    /// <summary>The memory chart's title figure: "281 MB / 1.87 GB" (its limit, or the machine's memory), or "281 MB" without one.</summary>
    public static string MemoryValue(StatsSample latest) =>
        latest.MemoryLimitBytes > 0
            ? $"{ByteFormat.Bytes(latest.MemoryBytes)} / {ByteFormat.Bytes(latest.MemoryLimitBytes)}"
            : ByteFormat.Bytes(latest.MemoryBytes);

    /// <summary>The CPU chart read aloud: "CPU usage 5.0%. Highest in the last 2 minutes: 30.0%."</summary>
    public static string CpuSummary(StatsHistory history) =>
        history.Latest is not { } latest
            ? NoFigures("CPU usage")
            : $"CPU usage {CpuValue(latest)}. Highest {Window}: {StatsSummary.Percent(Highest(history.Cpu))}.";

    /// <summary>The memory chart read aloud: "Memory usage 100 MB of 1.87 GB. Highest in the last 2 minutes: 300 MB."</summary>
    public static string MemorySummary(StatsHistory history)
    {
        if (history.Latest is not { } latest)
            return NoFigures("Memory usage");

        var of = latest.MemoryLimitBytes > 0 ? " of " + ByteFormat.Bytes(latest.MemoryLimitBytes) : "";
        return $"Memory usage {ByteFormat.Bytes(latest.MemoryBytes)}{of}. Highest {Window}: {ByteFormat.Bytes((long)Highest(history.Memory))}.";
    }

    /// <summary>The network chart read aloud: the latest and highest rate received and sent.</summary>
    public static string NetworkSummary(StatsHistory history) =>
        TwoSeries("Network I/O", "received", history.NetworkReceived, "sent", history.NetworkSent);

    /// <summary>The disk chart read aloud: the latest and highest rate read and written.</summary>
    public static string DiskSummary(StatsHistory history) =>
        TwoSeries("Disk read/write", "read", history.BlockRead, "written", history.BlockWritten);

    private static string TwoSeries(string title, string first, IReadOnlyList<StatsPoint> a, string second, IReadOnlyList<StatsPoint> b) =>
        a.Count == 0
            ? NoFigures(title)
            : $"{title}: {first} {ByteFormat.Rate(a[^1].Value)}, {second} {ByteFormat.Rate(b[^1].Value)}. "
              + $"Highest {Window}: {first} {ByteFormat.Rate(Highest(a))}, {second} {ByteFormat.Rate(Highest(b))}.";

    private static string NoFigures(string title) => title + ": no figures yet.";

    private static double Highest(IReadOnlyList<StatsPoint> points)
    {
        double highest = 0;
        foreach (var point in points)
            highest = Math.Max(highest, point.Value);
        return highest;
    }

    /// <summary>
    /// A byte axis up to <paramref name="bytes"/>: in the binary unit that keeps it under 1000, rounded up to a tick; when the
    /// rounding reaches 1000 the next unit is used ("1000 KB" reads "1 MB").
    /// </summary>
    private static ChartScale Bytes(double bytes, string suffix)
    {
        var unit = 0;
        var value = bytes;
        while (value >= 1000 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var max = Nice(value);
        if (max >= 1000 && unit < ByteUnits.Length - 1)
        {
            unit++;
            max = Nice(value / 1024);
        }

        return new ChartScale(max, Math.Pow(1024, unit), ByteUnits[unit] + suffix);
    }

    /// <summary>
    /// <paramref name="value"/> rounded up to a multiple of d3's tick step for <see cref="TickCount"/> ticks from 0, as d3's nice
    /// does: rounding up can change the step (1.47 rounds to 1.6, whose step is 0.5), so it rounds again until the result is a
    /// multiple of its own step, and the axis ends on a drawn tick.
    /// </summary>
    private static double Nice(double value)
    {
        if (value <= 0)
            return 1;

        var max = value;
        for (var i = 0; i < 10; i++)
        {
            var step = TickStep(max, TickCount);
            var next = Math.Round(Math.Ceiling((max / step) - 1e-9) * step, 10);
            if (next == max)
                break;
            max = next;
        }

        return max;
    }

    /// <summary>d3's tick increment from 0 to <paramref name="max"/>: 1, 2 or 5 times a power of ten.</summary>
    private static double TickStep(double max, int count)
    {
        var raw = max / count;
        var power = Math.Floor(Math.Log10(raw));
        var error = raw / Math.Pow(10, power);
        var factor = error >= Math.Sqrt(50) ? 10 : error >= Math.Sqrt(10) ? 5 : error >= Math.Sqrt(2) ? 2 : 1;
        return power >= 0 ? factor * Math.Pow(10, power) : factor / Math.Pow(10, -power);
    }
}

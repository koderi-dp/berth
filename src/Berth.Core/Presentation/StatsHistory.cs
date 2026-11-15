using System.Collections.Immutable;
using Berth.Engine.Models;

namespace Berth.Presentation;

/// <summary>One point of a stats chart.</summary>
public sealed record StatsPoint(DateTimeOffset Time, double Value);

/// <summary>
/// The last <see cref="Capacity"/> stats samples of a container as chart series. Immutable; each series is kept as it grows,
/// so adding a sample and reading a series are cheap.
/// </summary>
public sealed class StatsHistory
{
    /// <summary>The most samples a history holds; older ones are dropped.</summary>
    public const int Capacity = 120;

    /// <summary>A history with no samples.</summary>
    public static readonly StatsHistory Empty = new(null, [], [], [], [], [], []);

    private readonly ImmutableList<StatsPoint> _cpu;
    private readonly ImmutableList<StatsPoint> _memory;
    private readonly ImmutableList<StatsPoint> _networkReceived;
    private readonly ImmutableList<StatsPoint> _networkSent;
    private readonly ImmutableList<StatsPoint> _blockRead;
    private readonly ImmutableList<StatsPoint> _blockWritten;

    private StatsHistory(
        StatsSample? latest,
        ImmutableList<StatsPoint> cpu,
        ImmutableList<StatsPoint> memory,
        ImmutableList<StatsPoint> networkReceived,
        ImmutableList<StatsPoint> networkSent,
        ImmutableList<StatsPoint> blockRead,
        ImmutableList<StatsPoint> blockWritten)
    {
        Latest = latest;
        _cpu = cpu;
        _memory = memory;
        _networkReceived = networkReceived;
        _networkSent = networkSent;
        _blockRead = blockRead;
        _blockWritten = blockWritten;
    }

    /// <summary>The newest sample, or null before the first.</summary>
    public StatsSample? Latest { get; }

    /// <summary>CPU use in percent of one CPU.</summary>
    public IReadOnlyList<StatsPoint> Cpu => _cpu;

    /// <summary>Memory use in bytes.</summary>
    public IReadOnlyList<StatsPoint> Memory => _memory;

    /// <summary>Network bytes received per second.</summary>
    public IReadOnlyList<StatsPoint> NetworkReceived => _networkReceived;

    /// <summary>Network bytes sent per second.</summary>
    public IReadOnlyList<StatsPoint> NetworkSent => _networkSent;

    /// <summary>Block device bytes read per second.</summary>
    public IReadOnlyList<StatsPoint> BlockRead => _blockRead;

    /// <summary>Block device bytes written per second.</summary>
    public IReadOnlyList<StatsPoint> BlockWritten => _blockWritten;

    /// <summary>
    /// Adds a sample. A sample whose time is not after <see cref="Latest"/>'s is ignored, so the charts' times only go forward.
    /// A rate is the change from the previous sample divided by the seconds between them; it is 0 for the first sample and
    /// when a counter went down (the container restarted).
    /// </summary>
    public StatsHistory Add(StatsSample sample)
    {
        var previous = Latest;
        if (previous is not null && sample.Time <= previous.Time)
        {
            return this;
        }

        var seconds = previous is null ? 0 : (sample.Time - previous.Time).TotalSeconds;

        double Rate(Func<StatsSample, long> counter)
        {
            if (previous is null)
            {
                return 0;
            }

            var change = counter(sample) - counter(previous);
            return change < 0 ? 0 : change / seconds;
        }

        ImmutableList<StatsPoint> Next(ImmutableList<StatsPoint> series, double value)
        {
            var next = series.Add(new StatsPoint(sample.Time, value));
            return next.Count > Capacity ? next.RemoveAt(0) : next;
        }

        return new StatsHistory(
            sample,
            Next(_cpu, sample.CpuPercent),
            Next(_memory, sample.MemoryBytes),
            Next(_networkReceived, Rate(s => s.NetworkReceivedBytes)),
            Next(_networkSent, Rate(s => s.NetworkSentBytes)),
            Next(_blockRead, Rate(s => s.BlockReadBytes)),
            Next(_blockWritten, Rate(s => s.BlockWrittenBytes)));
    }
}

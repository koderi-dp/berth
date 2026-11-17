using System.Collections.Immutable;
using Berth.Engine.Models;

namespace Berth.State;

/// <summary>
/// The container a line of the Logs page came from: its id, name, and compose project and service when it has them. One instance per
/// container (until it is renamed), shared by all its lines.
/// </summary>
public sealed record LogOrigin(string ContainerId, string Name, string? Project, string? Service)
{
    /// <summary>What the page calls it: "project/service" for a compose service, otherwise the container's name.</summary>
    public string Label => Project is not null && Service is not null ? Project + "/" + Service : Name;
}

/// <summary>
/// One line of the Logs page: the line, the container it came from, and where it sorts: by <see cref="Time"/> to the second, then by
/// <see cref="Sequence"/>, the order lines arrived in.
/// </summary>
/// <param name="Time">The engine's timestamp of the line, or when it arrived for a line without one.</param>
public sealed record LogEntry(long Sequence, LogOrigin Origin, LogLine Line, DateTimeOffset Time)
{
    /// <summary>The Unix second of <see cref="Time"/>: lines within one second keep the order they arrived in.</summary>
    public long Second { get; } = Time.ToUnixTimeSeconds();
}

/// <summary>
/// The lines of every container on the Logs page, merged by time: the newest <see cref="Capacity"/> of them. Immutable; adding costs
/// time in proportion to the lines added (each placed by a binary search), not to the lines held.
/// </summary>
public sealed class MergedLog
{
    /// <summary>The most lines held; the oldest go first.</summary>
    public const int Capacity = 20_000;

    public static readonly MergedLog Empty = new([], 0);

    private MergedLog(ImmutableList<LogEntry> entries, long dropped)
    {
        Entries = entries;
        Dropped = dropped;
    }

    /// <summary>The lines held, oldest first.</summary>
    public ImmutableList<LogEntry> Entries { get; }

    /// <summary>How many lines the cap pushed out.</summary>
    public long Dropped { get; }

    /// <summary>Places <paramref name="entries"/> (in the order they arrived) by time, then drops the oldest beyond <see cref="Capacity"/>.</summary>
    public MergedLog Add(IReadOnlyList<LogEntry> entries)
    {
        if (entries.Count == 0)
            return this;

        var builder = Entries.ToBuilder();
        foreach (var entry in entries)
        {
            // Most lines are the newest yet: they go at the end without a search.
            if (builder.Count == 0 || Compare(builder[^1], entry) <= 0)
                builder.Add(entry);
            else
                builder.Insert(UpperBound(builder, entry), entry);
        }

        var excess = builder.Count - Capacity;
        if (excess > 0)
            builder.RemoveRange(0, excess);
        return new MergedLog(builder.ToImmutable(), Dropped + Math.Max(0, excess));
    }

    private static int Compare(LogEntry a, LogEntry b)
    {
        var bySecond = a.Second.CompareTo(b.Second);
        return bySecond != 0 ? bySecond : a.Sequence.CompareTo(b.Sequence);
    }

    /// <summary>The index of the first entry that sorts after <paramref name="entry"/>.</summary>
    private static int UpperBound(ImmutableList<LogEntry>.Builder entries, LogEntry entry)
    {
        int low = 0, high = entries.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (Compare(entries[middle], entry) <= 0)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }
}

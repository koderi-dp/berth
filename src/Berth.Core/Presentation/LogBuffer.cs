using System.Collections.Immutable;
using Berth.Engine.Models;

namespace Berth.Presentation;

/// <summary>
/// The log lines a page shows: a window over the newest <see cref="Capacity"/> lines of a log stream. Immutable; an append
/// costs time in proportion to the lines added, not to the lines held, so a buffer can be replaced on every batch.
/// </summary>
public sealed class LogBuffer
{
    /// <summary>The most lines a buffer holds; older ones are dropped.</summary>
    public const int Capacity = 10_000;

    /// <summary>A buffer with no lines, numbering from 1.</summary>
    public static readonly LogBuffer Empty = new([], 0, 1);

    private LogBuffer(ImmutableList<LogLine> lines, long dropped, long firstNumber)
    {
        Lines = lines;
        Dropped = dropped;
        FirstNumber = firstNumber;
    }

    /// <summary>The lines held, oldest first.</summary>
    public ImmutableList<LogLine> Lines { get; }

    /// <summary>How many lines the cap pushed out since the stream started or the view was last cleared.</summary>
    public long Dropped { get; }

    /// <summary>The 1-based number of <c>Lines[0]</c> since the stream started; when empty, the number the next line gets.</summary>
    public long FirstNumber { get; }

    /// <summary>Adds <paramref name="lines"/> after the held ones, dropping the oldest beyond <see cref="Capacity"/>.</summary>
    public LogBuffer Append(IReadOnlyList<LogLine> lines)
    {
        if (lines.Count == 0)
        {
            return this;
        }

        // Lines of this batch that would be dropped at once are never added.
        var skipped = Math.Max(0, lines.Count - Capacity);
        var added = skipped == 0 ? lines : lines.Skip(skipped);
        var kept = Lines.AddRange(added);
        var excess = kept.Count - Capacity;
        if (excess > 0)
        {
            kept = kept.RemoveRange(0, excess);
        }

        var dropped = skipped + Math.Max(0, excess);
        return new LogBuffer(kept, Dropped + dropped, FirstNumber + dropped);
    }

    /// <summary>Empties the view and resets <see cref="Dropped"/>; the next line keeps the number it would have had.</summary>
    public LogBuffer Clear() => new([], 0, FirstNumber + Lines.Count);
}

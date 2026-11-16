using System.Globalization;
using Berth.Engine.Models;
using Berth.Session;

namespace Berth.Presentation;

/// <summary>A search match by the number of its line since the stream started, so it can be found again after older lines are dropped.</summary>
public readonly record struct LogMatchAnchor(long LineNumber, int Start);

/// <summary>What Escape does on a container's page with the Logs tab shown.</summary>
public enum LogEscape
{
    /// <summary>Back to the list (the page's own Escape).</summary>
    Leave,

    /// <summary>Empty the search box; the search stays open.</summary>
    ClearTerm,

    /// <summary>Close the search bar (its box was already empty).</summary>
    CloseSearch,
}

/// <summary>
/// The Logs tab's search: whether its bar is open and the term in its box. The box only reports into it and starts from it when it
/// mounts, so the two cannot disagree; it closes only on an explicit close (the bar's close button, the toolbar's Search, Escape in
/// an empty box), never because the focus went elsewhere.
/// </summary>
public sealed record LogSearchState(bool Open, string Term)
{
    /// <summary>No search bar, no term.</summary>
    public static readonly LogSearchState Closed = new(Open: false, Term: "");

    /// <summary>The bar open; an open search keeps its term.</summary>
    public LogSearchState Opened() => Open ? this : new LogSearchState(Open: true, Term: "");

    /// <summary>The box reported <paramref name="term"/>; ignored while closed (a box being removed can still report).</summary>
    public LogSearchState WithTerm(string term) => !Open || term == Term ? this : this with { Term = term };

    /// <summary>The box emptied, the bar still open.</summary>
    public LogSearchState Cleared() => WithTerm("");
}

/// <summary>What the Logs tab decides: the texts it shows for each state, the search counter and its stepping, and whether it follows.</summary>
public static class LogView
{
    /// <summary>How long an empty log reads "Loading logs…" before it reads "No log output yet." (a followed log never says it opened).</summary>
    public static readonly TimeSpan LoadingTextTime = TimeSpan.FromSeconds(1);

    /// <summary>How close to the bottom, in pixels, still counts as at the bottom.</summary>
    private const double BottomTolerance = 8;

    private static readonly string Dropped =
        string.Create(CultureInfo.InvariantCulture, $"Showing the latest {LogBuffer.Capacity:N0} lines.");

    /// <summary>
    /// The text shown in place of the lines while there are none: "Loading logs…" for the first moments of a log that is
    /// starting (until <paramref name="waitedLong"/>), then "No log output yet."; a stopped container's empty log says it wrote
    /// nothing, or "Log cleared." when <paramref name="linesSeen"/> (lines came and the view was cleared). Null when there are
    /// lines, or when the log failed (its error says why).
    /// </summary>
    public static string? EmptyText(StreamStatus status, int lineCount, bool linesSeen, bool waitedLong)
    {
        if (lineCount > 0)
            return null;

        return status switch
        {
            StreamStatus.Failed => null,
            StreamStatus.Ended => linesSeen ? "Log cleared." : "The container stopped without any log output.",
            StreamStatus.Idle or StreamStatus.Loading when !waitedLong => "Loading logs…",
            _ => "No log output yet.",
        };
    }

    /// <summary>
    /// What Escape does: while the search box has the focus, it first empties the box, then closes the search; anywhere else it is
    /// the page's (back to the list).
    /// </summary>
    public static LogEscape Escape(LogSearchState search, bool boxFocused) =>
        !search.Open || !boxFocused ? LogEscape.Leave
        : search.Term.Length > 0 ? LogEscape.ClearTerm
        : LogEscape.CloseSearch;

    /// <summary>The notes shown above the lines: that a stopped container's lines are its last, and that older lines were dropped.</summary>
    public static IReadOnlyList<string> Notes(StreamStatus status, int lineCount, long dropped)
    {
        var notes = new List<string>(2);
        if (status == StreamStatus.Ended && lineCount > 0)
            notes.Add("The container stopped. These are its last lines.");
        if (dropped > 0)
            notes.Add(Dropped);
        return notes;
    }

    /// <summary>"3 of 17" for the 0-based <paramref name="current"/> match of <paramref name="count"/>; "No matches"; empty without a term.</summary>
    public static string Counter(string term, int current, int count)
    {
        if (string.IsNullOrWhiteSpace(term))
            return "";
        return count == 0
            ? "No matches"
            : string.Create(CultureInfo.InvariantCulture, $"{current + 1:N0} of {count:N0}");
    }

    /// <summary>The match after <paramref name="current"/>, the first after the last; -1 when there are none.</summary>
    public static int Next(int current, int count) => count == 0 ? -1 : current < 0 || current >= count - 1 ? 0 : current + 1;

    /// <summary>The match before <paramref name="current"/>, the last before the first; -1 when there are none.</summary>
    public static int Previous(int current, int count) => count == 0 ? -1 : current <= 0 || current >= count ? count - 1 : current - 1;

    /// <summary><paramref name="match"/> by its line's number, given the number of the buffer's first line.</summary>
    public static LogMatchAnchor Anchor(LogMatch match, long firstNumber) => new(firstNumber + match.Line, match.Start);

    /// <summary>
    /// The index of the current match: the one <paramref name="anchor"/> names; when it is gone, the next one after it, or the newest
    /// when none is after it. Without an anchor, the newest. -1 when there are no matches.
    /// </summary>
    public static int CurrentIndex(IReadOnlyList<LogMatch> matches, long firstNumber, LogMatchAnchor? anchor)
    {
        if (matches.Count == 0)
            return -1;
        if (anchor is not { } chosen)
            return matches.Count - 1;

        // Matches are in line order, and in text order on a line: the first one at or after the anchor.
        int low = 0, high = matches.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            var at = Anchor(matches[middle], firstNumber);
            if (at.LineNumber < chosen.LineNumber || (at.LineNumber == chosen.LineNumber && at.Start < chosen.Start))
                low = middle + 1;
            else
                high = middle;
        }

        return Math.Min(low, matches.Count - 1);
    }

    /// <summary>Where the matches on the 0-based <paramref name="line"/> start in <paramref name="matches"/>, and how many there are.</summary>
    public static (int First, int Count) MatchesOnLine(IReadOnlyList<LogMatch> matches, int line)
    {
        int low = 0, high = matches.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (matches[middle].Line < line)
                low = middle + 1;
            else
                high = middle;
        }

        var end = low;
        while (end < matches.Count && matches[end].Line == line)
            end++;
        return (low, end - low);
    }

    /// <summary>Whether a view scrolled to <paramref name="verticalOffset"/> of <paramref name="scrollableHeight"/> is at the bottom, so it follows new lines.</summary>
    public static bool IsAtBottom(double verticalOffset, double scrollableHeight) => scrollableHeight - verticalOffset <= BottomTolerance;

    /// <summary>
    /// Whether the view follows new lines after it moved from <paramref name="previousOffset"/> to <paramref name="verticalOffset"/>:
    /// it does at the bottom; above it, it keeps following only while it did not move up (new lines grow the log below the view
    /// without moving it, so only scrolling up stops following).
    /// </summary>
    public static bool Follows(bool following, double previousOffset, double verticalOffset, double scrollableHeight) =>
        IsAtBottom(verticalOffset, scrollableHeight) || (following && verticalOffset >= previousOffset);

    /// <summary>The line as shown: its text, after its UTC timestamp when <paramref name="timestamps"/> is on and it has one.</summary>
    public static string LineText(LogLine line, bool timestamps) =>
        timestamps && line.Timestamp is { } time ? LogSearch.Timestamp(time) + " " + line.Text : line.Text;

    /// <summary>Where the line's own text starts in <see cref="LineText"/>, so a match in it can be drawn there.</summary>
    public static int TextOffset(LogLine line, bool timestamps) =>
        timestamps && line.Timestamp is { } time ? LogSearch.Timestamp(time).Length + 1 : 0;
}

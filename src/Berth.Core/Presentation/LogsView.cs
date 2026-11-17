using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Berth.Engine.Models;
using Berth.Session;
using Berth.State;

namespace Berth.Presentation;

/// <summary>
/// The Logs page's filter: which containers or compose projects (<see cref="Objects"/> keys, see <see cref="LogsView.ObjectKey"/>; null
/// for all), which streams, and whether only running containers' lines show.
/// </summary>
public sealed record LogsFilter(ImmutableHashSet<string>? Objects, bool Stdout, bool Stderr, bool RunningOnly)
{
    /// <summary>Everything shows.</summary>
    public static readonly LogsFilter All = new(Objects: null, Stdout: true, Stderr: true, RunningOnly: false);

    /// <summary>Whether it hides anything.</summary>
    public bool IsActive => Objects is not null || !Stdout || !Stderr || RunningOnly;

    /// <summary>Whether a line of <paramref name="entry"/>'s stream and container shows.</summary>
    public bool Shows(LogEntry entry, LogSource? source)
    {
        if (entry.Line.Stream == LogStream.Stdout ? !Stdout : !Stderr)
            return false;
        if (RunningOnly && source is not { Running: true, Removed: false })
            return false;
        return Objects is null || Objects.Contains(LogsView.ObjectKey(entry.Origin));
    }

    /// <summary>The filter with the object <paramref name="key"/> shown or hidden, out of <paramref name="all"/>.</summary>
    public LogsFilter WithObject(string key, bool shown, IReadOnlyCollection<string> all)
    {
        var current = Objects ?? [.. all];
        var next = shown ? current.Add(key) : current.Remove(key);
        return this with { Objects = all.All(next.Contains) ? null : next };
    }
}

/// <summary>A choice in the filter's checklist: a compose project or a container that is not in one.</summary>
/// <param name="Key">What the filter keeps (<see cref="LogsView.ObjectKey"/>).</param>
public sealed record LogObject(string Key, string Label, bool IsProject);

/// <summary>What the Logs page shows: its lines after the filter and search, and its line counts, notes and empty texts.</summary>
public sealed record LogsShown(IReadOnlyList<LogEntry> Entries, int Total, string? QueryError);

/// <summary>What the Logs page decides: which lines it shows, how it words them, and the texts around them.</summary>
public static class LogsView
{
    /// <summary>The key of the filter choice a line belongs to: its compose project, or its container.</summary>
    public static string ObjectKey(LogOrigin origin) =>
        origin.Project is { } project ? "project:" + project : "container:" + origin.ContainerId;

    /// <summary>The filter's choices: every compose project and every container outside one with a stream on the page, by name.</summary>
    public static ImmutableArray<LogObject> Objects(LogsState state)
    {
        var objects = new Dictionary<string, LogObject>(StringComparer.Ordinal);
        foreach (var source in state.Sources.Values)
        {
            var origin = source.Origin;
            var key = ObjectKey(origin);
            if (!objects.ContainsKey(key))
                objects[key] = new LogObject(key, origin.Project ?? origin.Name, origin.Project is not null);
        }

        return [.. objects.Values.OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The lines that show: those <paramref name="filter"/> keeps and <paramref name="query"/> matches. With neither, the log itself
    /// (no copy). A search that ran out of time shows nothing and says so.
    /// </summary>
    public static LogsShown Shown(LogsState state, LogsFilter filter, LogsQuery query)
    {
        var entries = state.Log.Entries;
        if (query.Error is { } error)
            return new LogsShown([], entries.Count, error);
        if (!filter.IsActive && query.IsEmpty)
            return new LogsShown(entries, entries.Count, null);

        IEnumerable<LogEntry> kept = filter.IsActive
            ? entries.Where(entry => filter.Shows(entry, state.Sources.GetValueOrDefault(entry.Origin.ContainerId)))
            : entries;
        if (query.IsEmpty)
            return new LogsShown([.. kept], entries.Count, null);

        var matched = query.Filter(kept, entry => entry.Line.Text);
        return matched is null
            ? new LogsShown([], entries.Count, LogsQuery.TooSlowText)
            : new LogsShown(matched, entries.Count, null);
    }

    /// <summary>"274 lines", or "41 of 274 lines" when the filter or search hides some.</summary>
    public static string CountText(int shown, int total, bool narrowed) =>
        narrowed
            ? string.Create(CultureInfo.InvariantCulture, $"{shown:N0} of {total:N0} {(total == 1 ? "line" : "lines")}")
            : string.Create(CultureInfo.InvariantCulture, $"{total:N0} {(total == 1 ? "line" : "lines")}");

    /// <summary>The notes above the table: that the engine is out of reach, that older lines were dropped, and that some logs are not followed.</summary>
    public static IReadOnlyList<string> Notes(LogsState state, bool waiting)
    {
        var notes = new List<string>(3);
        if (waiting)
            notes.Add(DetailPage.WaitingText);
        if (state.Log.Dropped > 0)
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"Showing the latest {MergedLog.Capacity:N0} lines; older ones were dropped."));
        if (state.NotFollowed > 0)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{state.NotFollowed:N0} running {(state.NotFollowed == 1 ? "container is" : "containers are")} not followed: at most {LogsReducer.MaxStreams} logs are followed at once."));
        }

        var failed = state.Sources.Values.Count(s => s.Status == LogSourceStatus.Failed && s.Running);
        if (failed > 0)
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{failed:N0} {(failed == 1 ? "log" : "logs")} could not be read."));
        return notes;
    }

    /// <summary>
    /// The text in the table's place when no line shows: no containers on the engine, none running yet, no lines yet, or nothing matches.
    /// Null when lines show, or while the container list is not known yet (<paramref name="loading"/> shows instead).
    /// </summary>
    public static string? EmptyText(LogsState state, int shown, bool waiting)
    {
        if (shown > 0)
            return null;
        if (state.Log.Entries.Count > 0)
            return "No lines match the search or filter.";
        if (waiting)
            return DetailPage.WaitingText;
        if (!state.ContainersKnown)
            return "Loading logs…";
        if (!state.EngineHasContainers)
            return "There are no containers on this engine.";
        if (!state.Sources.Values.Any(s => s.Running))
            return "No container is running. Logs show here when one starts.";
        return "No log lines yet.";
    }

    /// <summary>A line's time in the table: local, to the second, as "2026-11-17 12:21:21".</summary>
    public static string Time(LogEntry entry) =>
        entry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A line's time when it is expanded, copied or saved: local, to the millisecond, as "2026-11-17 12:21:21.123".</summary>
    public static string FullTime(LogEntry entry) =>
        entry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>The stream's name: "stdout" or "stderr".</summary>
    public static string StreamName(LogStream stream) => stream == LogStream.Stderr ? "stderr" : "stdout";

    /// <summary>
    /// The lines as text, one per line, as copied or saved: the time to the millisecond when <paramref name="timestamps"/>, the
    /// container's label when <paramref name="objects"/>, then the message.
    /// </summary>
    public static string CopyText(IEnumerable<LogEntry> entries, bool timestamps, bool objects)
    {
        var text = new StringBuilder();
        var first = true;
        foreach (var entry in entries)
        {
            if (!first)
                text.Append(Environment.NewLine);
            first = false;
            if (timestamps)
                text.Append(FullTime(entry)).Append(' ');
            if (objects)
                text.Append(entry.Origin.Label).Append(' ');
            text.Append(entry.Line.Text);
        }

        return text.ToString();
    }

    /// <summary>The name a save of the shown lines suggests: "logs-20261117-122121".</summary>
    public static string SaveName(DateTimeOffset now) =>
        "logs-" + now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}

/// <summary>
/// The colour each container's name is drawn in on the Logs page: one of eight, picked from its id so it stays the same, each in a light
/// and a dark form that reads on the theme's background (at least 4.5:1 on the light #f3f3f3 and the dark #202020 surfaces).
/// </summary>
public static class LogsPalette
{
    private static readonly int[] Light = [0x0F6CBD, 0x107C10, 0x6B4FA0, 0xA0308F, 0xB04A0B, 0x036C70, 0x7A5B00, 0x3F57C8];
    private static readonly int[] Dark = [0x62ABF5, 0x6CCB5F, 0xB69DE0, 0xE88BD8, 0xF7955A, 0x4CC2C4, 0xE3B341, 0x8EA0F5];

    /// <summary>How many colours there are.</summary>
    public static int Count => Light.Length;

    /// <summary>The colour of the container <paramref name="containerId"/>: the same on every run (FNV-1a of its id).</summary>
    public static int Index(string containerId)
    {
        var hash = 2166136261u;
        foreach (var c in containerId)
            hash = unchecked((hash ^ c) * 16777619u);
        return (int)(hash % (uint)Light.Length);
    }

    /// <summary>The colour <paramref name="index"/> as 0xRRGGBB for the light or dark theme.</summary>
    public static int Rgb(int index, bool dark) => (dark ? Dark : Light)[index];
}

/// <summary>What the app does with the Logs page's session.</summary>
public enum LogsSessionStep
{
    /// <summary>Leave it as it is.</summary>
    Keep,

    /// <summary>Open one (in place of one cut off from its connection).</summary>
    Open,

    /// <summary>Close it now: the window is hidden to the tray.</summary>
    Close,

    /// <summary>Close it once the page has not been shown for <see cref="LogsLifecycle.LeftLimit"/>.</summary>
    CloseLater,
}

/// <summary>When the Logs page's session runs: while the page is shown, a while after it is left, never while the window is hidden.</summary>
public static class LogsLifecycle
{
    /// <summary>How long the session keeps following after the page is left (or the window minimized), so a quick look elsewhere loses nothing.</summary>
    public static readonly TimeSpan LeftLimit = TimeSpan.FromMinutes(1);

    /// <summary>
    /// What to do: close at once while the window is <paramref name="hidden"/> to the tray; while the page is not
    /// <paramref name="shown"/>, close later; while shown and <paramref name="connected"/>, open one when there is none or the one there
    /// lost its connection (a lost one stays to show its lines while not connected).
    /// </summary>
    public static LogsSessionStep Next(bool shown, bool hidden, bool connected, bool open, bool cutOff)
    {
        if (hidden)
            return open ? LogsSessionStep.Close : LogsSessionStep.Keep;
        if (!shown)
            return open ? LogsSessionStep.CloseLater : LogsSessionStep.Keep;
        return connected && (!open || cutOff) ? LogsSessionStep.Open : LogsSessionStep.Keep;
    }
}

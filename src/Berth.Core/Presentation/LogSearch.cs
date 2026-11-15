using System.Globalization;
using System.Text;
using Berth.Engine.Models;

namespace Berth.Presentation;

/// <summary>A search match: the 0-based index of the line, and where in its text the match starts and how long it is.</summary>
public sealed record LogMatch(int Line, int Start, int Length);

/// <summary>Searching log lines and the text the Logs tab copies.</summary>
public static class LogSearch
{
    /// <summary>
    /// Every occurrence of <paramref name="term"/> in the lines' text, ignoring case, in line order; matches on one line do
    /// not overlap. A blank term finds nothing.
    /// </summary>
    public static IReadOnlyList<LogMatch> Find(IReadOnlyList<LogLine> lines, string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var matches = new List<LogMatch>();
        for (var line = 0; line < lines.Count; line++)
        {
            var text = lines[line].Text;
            var start = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            while (start >= 0)
            {
                matches.Add(new LogMatch(line, start, term.Length));
                start = text.IndexOf(term, start + term.Length, StringComparison.OrdinalIgnoreCase);
            }
        }

        return matches;
    }

    /// <summary>
    /// The lines' text, one per line. With <paramref name="timestamps"/>, a line that has a timestamp starts with it in UTC,
    /// as "2026-11-16T10:00:00.0000000Z ".
    /// </summary>
    public static string CopyText(IReadOnlyList<LogLine> lines, bool timestamps)
    {
        var text = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                text.Append(Environment.NewLine);
            }

            if (timestamps && lines[i].Timestamp is { } time)
            {
                text.Append(time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)).Append(' ');
            }

            text.Append(lines[i].Text);
        }

        return text.ToString();
    }
}

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Berth.Presentation;

/// <summary>
/// The Logs page's search: plain text, or a regular expression when written as <c>/…/</c>; case-insensitive unless
/// <c>matchCase</c>. A pattern that is not valid matches nothing and says why (<see cref="Error"/>). Every regular expression runs
/// under <see cref="MatchTimeout"/>, and a search over many lines under <see cref="SearchBudget"/>, so a pattern that backtracks
/// without end cannot freeze the page.
/// </summary>
public sealed class LogsQuery
{
    /// <summary>The longest one line's match may take.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest a search over every line may take before it stops and matches nothing.</summary>
    public static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(1);

    /// <summary>What a search that ran out of time says.</summary>
    public const string TooSlowText = "The pattern took too long and was stopped. Try a simpler one.";

    /// <summary>No search: every line matches.</summary>
    public static readonly LogsQuery None = new("", null, null, StringComparison.OrdinalIgnoreCase);

    private readonly string _term;
    private readonly Regex? _regex;
    private readonly StringComparison _comparison;

    private LogsQuery(string term, Regex? regex, string? error, StringComparison comparison)
    {
        _term = term;
        _regex = regex;
        Error = error;
        _comparison = comparison;
    }

    /// <summary>Why the pattern matches nothing (not a valid regular expression); null otherwise.</summary>
    public string? Error { get; }

    /// <summary>Whether there is no search, so every line matches.</summary>
    public bool IsEmpty => Error is null && _regex is null && _term.Length == 0;

    /// <summary>Whether the search is a regular expression.</summary>
    public bool IsRegex => _regex is not null;

    /// <summary>The search for the box's <paramref name="text"/>: <c>/pattern/</c> is a regular expression, anything else plain text.</summary>
    public static LogsQuery Parse(string text, bool matchCase)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.IsNullOrEmpty(text))
            return None;

        if (text.Length < 2 || text[0] != '/' || text[^1] != '/')
            return new LogsQuery(text, null, null, comparison);

        var pattern = text[1..^1];
        if (pattern.Length == 0)
            return None;

        try
        {
            var options = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            return new LogsQuery("", new Regex(pattern, options, MatchTimeout), null, comparison);
        }
        catch (ArgumentException ex)
        {
            return new LogsQuery("", null, "Not a valid regular expression: " + ex.Message, comparison);
        }
    }

    /// <summary>Whether <paramref name="text"/> matches. Throws <see cref="RegexMatchTimeoutException"/> when a pattern runs too long.</summary>
    public bool IsMatch(string text) =>
        Error is null && (_regex is { } regex ? regex.IsMatch(text) : _term.Length == 0 || text.Contains(_term, _comparison));

    /// <summary>
    /// The matches in <paramref name="text"/>, in order and not overlapping, to mark them; none for an empty match. A pattern that runs
    /// too long marks nothing.
    /// </summary>
    public IReadOnlyList<(int Start, int Length)> Find(string text)
    {
        if (Error is not null || IsEmpty)
            return [];

        var found = new List<(int, int)>();
        if (_regex is { } regex)
        {
            try
            {
                for (var match = regex.Match(text); match.Success; match = match.NextMatch())
                {
                    if (match.Length > 0)
                        found.Add((match.Index, match.Length));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return [];
            }

            return found;
        }

        var start = text.IndexOf(_term, _comparison);
        while (start >= 0)
        {
            found.Add((start, _term.Length));
            start = text.IndexOf(_term, start + _term.Length, _comparison);
        }

        return found;
    }

    /// <summary>
    /// Keeps the items whose text matches, in order. Null when the search ran out of time (one line over <see cref="MatchTimeout"/>, or
    /// all of them over <see cref="SearchBudget"/>): it then matches nothing and says so (<see cref="TooSlowText"/>).
    /// </summary>
    public List<T>? Filter<T>(IEnumerable<T> items, Func<T, string> text)
    {
        var kept = new List<T>();
        var clock = Stopwatch.StartNew();
        var checkedCount = 0;
        try
        {
            foreach (var item in items)
            {
                if (IsMatch(text(item)))
                    kept.Add(item);
                if (_regex is not null && ++checkedCount % 256 == 0 && clock.Elapsed > SearchBudget)
                    return null;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return kept;
    }
}

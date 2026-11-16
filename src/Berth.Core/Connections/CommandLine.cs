using System.Collections.Immutable;
using System.Text;

namespace Berth.Connections;

/// <summary>A command line split into the program and its arguments, or why it cannot be (then both are empty).</summary>
public sealed record ParsedCommandLine(string Program, ImmutableArray<string> Arguments, string? Error);

/// <summary>
/// A command line as the user types it on Windows (an engine control command), split into the program and its arguments the way the
/// Windows C runtime splits a process's command line, so it means what it would mean typed in a console window, without a shell:
/// nothing is expanded, redirected or chained. It lives in <c>Connections</c>, not <c>Text</c>, because the connection draft checks it.
/// </summary>
/// <remarks>
/// The Exec tab's custom command parser splits a command for a Linux shell, where a backslash escapes the next
/// character; here a backslash is a Windows path separator and stays itself unless quotes follow it.
/// </remarks>
public static class CommandLine
{
    public const string Blank = "Enter a command.";
    public const string OpenQuote = "Close the quote.";
    public const string LineBreak = "Put the command on one line.";

    private static readonly ParsedCommandLine BlankLine = new("", [], Blank);
    private static readonly ParsedCommandLine OpenQuoteLine = new("", [], OpenQuote);
    private static readonly ParsedCommandLine LineBreakLine = new("", [], LineBreak);

    /// <summary>
    /// Splits <paramref name="text"/>: words are separated by spaces and tabs outside quotes; double quotes keep spaces in a word and two
    /// of them inside quotes are one quote; backslashes are literal except before a quote, where each pair is one backslash and an odd
    /// one left over makes the quote literal. An empty line, an empty program, a quote left open or a line break is refused.
    /// </summary>
    public static ParsedCommandLine Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.AsSpan().IndexOfAny('\r', '\n') >= 0)
            return LineBreakLine;

        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                var run = 1;
                while (i + run < text.Length && text[i + run] == '\\')
                    run++;
                inWord = true;
                if (i + run < text.Length && text[i + run] == '"')
                {
                    word.Append('\\', run / 2);
                    if (run % 2 == 1)
                    {
                        word.Append('"');
                        i += run;
                    }
                    else
                    {
                        // The quote after an even run delimits: the loop sees it next.
                        i += run - 1;
                    }
                }
                else
                {
                    word.Append('\\', run);
                    i += run - 1;
                }

                continue;
            }

            if (c == '"')
            {
                inWord = true;
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                {
                    word.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (!quoted && c is ' ' or '\t')
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }

                continue;
            }

            word.Append(c);
            inWord = true;
        }

        if (quoted)
            return OpenQuoteLine;
        if (inWord)
            words.Add(word.ToString());
        if (words.Count == 0 || words[0].Length == 0)
            return BlankLine;

        return new ParsedCommandLine(words[0], [.. words.Skip(1)], null);
    }

    /// <summary>The command line that <see cref="Parse"/> splits back into <paramref name="program"/> and <paramref name="arguments"/>.</summary>
    public static string Format(string program, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { program }.Concat(arguments).Select(Quote));

    /// <summary>A word bare when it has no space, tab or quote; otherwise in quotes, with backslashes doubled only before a quote.</summary>
    private static string Quote(string word)
    {
        if (word.Length > 0 && word.AsSpan().IndexOfAny(' ', '\t', '"') < 0)
            return word;

        var text = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in word)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes before a quote are doubled, and the quote escaped; before anything else they stay as they are.
            text.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            text.Append(c);
        }

        // Before the closing quote they are doubled, so it still closes.
        return text.Append('\\', backslashes * 2).Append('"').ToString();
    }
}

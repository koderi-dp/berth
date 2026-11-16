using System.Collections.Immutable;
using System.Text;

namespace Berth.Presentation;

/// <summary>A shell the Exec tab's picker offers; the custom one has no command of its own (the user types it).</summary>
public sealed record ShellChoice(string Label, ImmutableArray<string> Command, bool IsCustom);

/// <summary>A typed command split into the words sent to the engine, or why it cannot be (then <see cref="Command"/> is empty).</summary>
public sealed record ParsedCommand(ImmutableArray<string> Command, string? Error);

/// <summary>The shells the Exec tab offers, and how a custom command is split into words.</summary>
public static class ExecCommand
{
    /// <summary>sh first (every image that has a shell has it), then bash, ash, and a custom command.</summary>
    public static readonly IReadOnlyList<ShellChoice> Choices =
    [
        new("sh", ["sh"], IsCustom: false),
        new("bash", ["bash"], IsCustom: false),
        new("ash", ["ash"], IsCustom: false),
        new("Custom command…", [], IsCustom: true),
    ];

    private static readonly ParsedCommand Blank = new([], "Enter a command.");

    private static readonly ParsedCommand OpenQuote = new([], "Close the quote.");

    /// <summary>
    /// Splits <paramref name="text"/> into words as a POSIX shell would, without running one: words are separated by spaces and
    /// tabs; single quotes keep everything literally; double quotes allow <c>\"</c> and <c>\\</c> (any other backslash stays); a
    /// backslash outside quotes keeps the next character. The words go to the engine as an array.
    /// </summary>
    public static ParsedCommand Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = ImmutableArray.CreateBuilder<string>();
        var word = new StringBuilder();
        var inWord = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case ' ' or '\t':
                    if (inWord)
                    {
                        words.Add(word.ToString());
                        word.Clear();
                        inWord = false;
                    }

                    break;

                case '\'':
                {
                    var end = text.IndexOf('\'', i + 1);
                    if (end < 0)
                        return OpenQuote;
                    word.Append(text, i + 1, end - i - 1);
                    inWord = true;
                    i = end;
                    break;
                }

                case '"':
                {
                    var end = DoubleQuoted(text, i + 1, word);
                    if (end < 0)
                        return OpenQuote;
                    inWord = true;
                    i = end;
                    break;
                }

                case '\\':
                    // A backslash at the very end has nothing to keep: it stays itself.
                    word.Append(i + 1 < text.Length ? text[++i] : c);
                    inWord = true;
                    break;

                default:
                    word.Append(c);
                    inWord = true;
                    break;
            }
        }

        if (inWord)
            words.Add(word.ToString());

        return words.Count == 0 ? Blank : new ParsedCommand(words.ToImmutable(), null);
    }

    /// <summary>Appends a double-quoted part starting at <paramref name="start"/>; returns the closing quote's index, or -1.</summary>
    private static int DoubleQuoted(string text, int start, StringBuilder word)
    {
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
                return i;
            if (c == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\')
                c = text[++i];
            word.Append(c);
        }

        return -1;
    }
}

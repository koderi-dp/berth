using System.Collections.Immutable;
using System.Text;
using Berth.Engine.Models;

namespace Berth.Engine;

/// <summary>Turns terminal output into plain text, and its colour codes into styled spans over that text.</summary>
public static class AnsiText
{
    private const char Escape = '\u001b';
    private const char Bell = '\u0007';

    /// <summary>
    /// <paramref name="text"/> without escape sequences (CSI such as colours, cursor moves and erases; OSC such as window
    /// titles; and the short ESC sequences) and without any other control character except tab. An escape sequence cut off by the
    /// end of the text is dropped. Text with nothing to remove is returned as it is.
    /// </summary>
    public static string Strip(string text)
    {
        var first = FirstControl(text);
        if (first < 0)
        {
            return text;
        }

        var plain = new StringBuilder(text.Length);
        plain.Append(text, 0, first);
        for (var i = first; i < text.Length; i++)
        {
            var c = text[i];
            if (c == Escape)
            {
                i = EndOfEscape(text, i);
            }
            else if (c == '\t' || !char.IsControl(c))
            {
                plain.Append(c);
            }
        }

        return plain.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> as <see cref="Strip"/> leaves it, with the styles its SGR codes (<c>ESC[…m</c>) set kept as spans over
    /// that plain text: reset, bold, dim, italic, underline, the sixteen colours, 256 colours (<c>38;5;n</c>) and 24-bit colours
    /// (<c>38;2;r;g;b</c>), for text and background, and their defaults. Other codes are dropped. The text starts plain: no style
    /// carries over from an earlier text. Text without any escape sequence is returned as it is, with no spans.
    /// </summary>
    public static (string Text, ImmutableArray<TextSpan> Spans) Style(string text)
    {
        var first = FirstControl(text);
        if (first < 0)
        {
            return (text, ImmutableArray<TextSpan>.Empty);
        }

        var plain = new StringBuilder(text.Length);
        plain.Append(text, 0, first);
        var spans = new Spans();
        for (var i = first; i < text.Length; i++)
        {
            var c = text[i];
            if (c == Escape)
            {
                var end = EndOfEscape(text, i);
                if (IsSgr(text, i, end))
                {
                    spans.Change(plain.Length, Apply(spans.Current, text.AsSpan(i + 2, end - i - 2)));
                }

                i = end;
            }
            else if (c == '\t' || !char.IsControl(c))
            {
                plain.Append(c);
            }
        }

        return (plain.ToString(), spans.Finish(plain.Length));
    }

    /// <summary>Whether the escape sequence from <paramref name="start"/> to <paramref name="end"/> is a whole SGR: ESC [ digits and semicolons m.</summary>
    private static bool IsSgr(string text, int start, int end)
    {
        if (end <= start + 1 || text[start + 1] != '[' || text[end] != 'm')
        {
            return false;
        }

        for (var i = start + 2; i < end; i++)
        {
            if (text[i] is not ((>= '0' and <= '9') or ';'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><paramref name="style"/> changed by the SGR parameters <paramref name="parameters"/> ("1;31", "" for a reset).</summary>
    private static TextStyle Apply(TextStyle style, ReadOnlySpan<char> parameters)
    {
        Span<int> values = stackalloc int[16];
        var count = 0;
        var value = 0;
        var tooMany = false;
        foreach (var c in parameters)
        {
            if (c == ';')
            {
                Add(values, ref count, value, ref tooMany);
                value = 0;
            }
            else
            {
                value = Math.Min((value * 10) + (c - '0'), 100_000);
            }
        }

        Add(values, ref count, value, ref tooMany);
        if (tooMany)
        {
            return style;
        }

        var (fore, back, effects) = (style.Foreground, style.Background, style.Effects);
        for (var i = 0; i < count; i++)
        {
            var code = values[i];
            switch (code)
            {
                case 0:
                    (fore, back, effects) = (AnsiColor.Default, AnsiColor.Default, TextEffects.None);
                    break;
                case 1:
                    effects |= TextEffects.Bold;
                    break;
                case 2:
                    effects |= TextEffects.Dim;
                    break;
                case 3:
                    effects |= TextEffects.Italic;
                    break;
                case 4:
                    effects |= TextEffects.Underline;
                    break;
                case 22:
                    effects &= ~(TextEffects.Bold | TextEffects.Dim);
                    break;
                case 23:
                    effects &= ~TextEffects.Italic;
                    break;
                case 24:
                    effects &= ~TextEffects.Underline;
                    break;
                case >= 30 and <= 37:
                    fore = AnsiColor.FromIndex(code - 30);
                    break;
                case 39:
                    fore = AnsiColor.Default;
                    break;
                case >= 40 and <= 47:
                    back = AnsiColor.FromIndex(code - 40);
                    break;
                case 49:
                    back = AnsiColor.Default;
                    break;
                case >= 90 and <= 97:
                    fore = AnsiColor.FromIndex(code - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    back = AnsiColor.FromIndex(code - 100 + 8);
                    break;
                case 38 or 48:
                    if (Extended(values[(i + 1)..count], out var color, out var used))
                    {
                        if (code == 38)
                            fore = color;
                        else
                            back = color;
                    }

                    i += used;
                    break;
            }
        }

        return new TextStyle(fore, back, effects);
    }

    private static void Add(Span<int> values, ref int count, int value, ref bool tooMany)
    {
        if (count == values.Length)
        {
            tooMany = true;
            return;
        }

        values[count++] = value;
    }

    /// <summary>The colour after a 38 or 48: <c>5;n</c> or <c>2;r;g;b</c>; <paramref name="used"/> is how many parameters it took.</summary>
    private static bool Extended(ReadOnlySpan<int> rest, out AnsiColor color, out int used)
    {
        color = AnsiColor.Default;
        switch (rest)
        {
            case [5, var index, ..]:
                used = 2;
                if (index > 255)
                    return false;
                color = AnsiColor.FromIndex(index);
                return true;
            case [2, var r, var g, var b, ..]:
                used = 4;
                if (r > 255 || g > 255 || b > 255)
                    return false;
                color = AnsiColor.FromRgb(r, g, b);
                return true;
            default:
                // Malformed: the rest of the sequence cannot be read safely.
                used = rest.Length;
                return false;
        }
    }

    /// <summary>The spans a text's styles make, as the plain text grows.</summary>
    private sealed class Spans
    {
        private ImmutableArray<TextSpan>.Builder? _spans;
        private int _start;

        public TextStyle Current { get; private set; } = TextStyle.Plain;

        /// <summary>The style changes to <paramref name="style"/> at <paramref name="position"/> of the plain text.</summary>
        public void Change(int position, TextStyle style)
        {
            if (style == Current)
            {
                return;
            }

            Close(position);
            Current = style;
            _start = position;
        }

        public ImmutableArray<TextSpan> Finish(int length)
        {
            Close(length);
            return _spans is null ? ImmutableArray<TextSpan>.Empty : _spans.ToImmutable();
        }

        private void Close(int position)
        {
            if (position == _start || Current.IsPlain)
            {
                return;
            }

            _spans ??= ImmutableArray.CreateBuilder<TextSpan>();
            if (_spans.Count > 0 && _spans[^1] is var last && last.Style == Current && last.Start + last.Length == _start)
            {
                _spans[^1] = last with { Length = position - last.Start };
            }
            else
            {
                _spans.Add(new TextSpan(_start, position - _start, Current));
            }
        }
    }

    private static int FirstControl(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\t' && char.IsControl(text[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The index of the last character of the escape sequence that starts at <paramref name="start"/>.</summary>
    private static int EndOfEscape(string text, int start)
    {
        var next = start + 1;
        if (next >= text.Length)
        {
            return start;
        }

        return text[next] switch
        {
            '[' => EndOfCsi(text, next + 1),
            ']' => EndOfOsc(text, next + 1),
            _ => EndOfShort(text, next),
        };
    }

    /// <summary>CSI: parameter bytes 0x30-0x3F, intermediate bytes 0x20-0x2F, then one final byte 0x40-0x7E.</summary>
    private static int EndOfCsi(string text, int i)
    {
        while (i < text.Length && text[i] is >= '0' and <= '?')
        {
            i++;
        }

        return EndOfFinal(text, i, '@');
    }

    /// <summary>The short ESC sequences: intermediate bytes 0x20-0x2F, then one final byte 0x30-0x7E.</summary>
    private static int EndOfShort(string text, int i) => EndOfFinal(text, i, '0');

    private static int EndOfFinal(string text, int i, char lowestFinal)
    {
        while (i < text.Length && text[i] is >= ' ' and <= '/')
        {
            i++;
        }

        if (i >= text.Length)
        {
            return text.Length - 1;
        }

        // A malformed sequence ends before the character that broke it, which is then read as text.
        return text[i] >= lowestFinal && text[i] <= '~' ? i : i - 1;
    }

    /// <summary>OSC: anything up to BEL, ST (ESC \) or the 8-bit ST.</summary>
    private static int EndOfOsc(string text, int i)
    {
        for (; i < text.Length; i++)
        {
            if (text[i] is Bell or '\u009c')
            {
                return i;
            }

            if (text[i] == Escape && i + 1 < text.Length && text[i + 1] == '\\')
            {
                return i + 1;
            }
        }

        return text.Length - 1;
    }
}

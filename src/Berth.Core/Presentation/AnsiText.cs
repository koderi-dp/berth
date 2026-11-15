using System.Text;

namespace Berth.Presentation;

/// <summary>Turns terminal output into plain text.</summary>
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

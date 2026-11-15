using System.Globalization;

namespace Berth.Engine;

/// <summary>Reads the engine's RFC 3339 times, which carry up to nine fractional digits (Go's RFC3339Nano).</summary>
internal static class EngineTime
{
    private static readonly string[] Formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"];

    /// <summary>
    /// The time in <paramref name="text"/>, or null when it is not an RFC 3339 time or is the engine's zero time
    /// (<c>0001-01-01T00:00:00Z</c>, meaning "never"). Digits past the seventh fractional one are dropped.
    /// </summary>
    public static DateTimeOffset? Parse(ReadOnlySpan<char> text)
    {
        // Guard the shape first: the invariant culture's parser would otherwise accept things like "10:30" in a log line.
        if (text.Length < 20 || text[4] != '-' || text[7] != '-' || text[10] != 'T' || text[13] != ':' || text[16] != ':')
        {
            return null;
        }

        Span<char> trimmed = stackalloc char[Math.Min(text.Length, 64)];
        if (text.Length > trimmed.Length)
        {
            return null;
        }

        var length = DropExtraFractionDigits(text, trimmed);
        if (!DateTimeOffset.TryParseExact(
                trimmed[..length], Formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
        {
            return null;
        }

        return time.Year == 1 ? null : time;
    }

    /// <summary>Copies <paramref name="text"/> into <paramref name="into"/> keeping at most seven fractional digits.</summary>
    private static int DropExtraFractionDigits(ReadOnlySpan<char> text, Span<char> into)
    {
        var written = 0;
        var digitsAfterDot = -1;
        foreach (var c in text)
        {
            if (digitsAfterDot >= 0)
            {
                if (char.IsAsciiDigit(c))
                {
                    if (++digitsAfterDot > 7)
                    {
                        continue;
                    }
                }
                else
                {
                    digitsAfterDot = -1;
                }
            }
            else if (c == '.' && written == 19)
            {
                digitsAfterDot = 0;
            }

            into[written++] = c;
        }

        return written;
    }
}

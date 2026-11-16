using System.Text;

namespace Berth.Processes;

/// <summary>Text a console program wrote: <c>wsl.exe</c> writes its own messages in UTF-16, the programs it runs write UTF-8.</summary>
public static class ConsoleText
{
    /// <summary>
    /// <paramref name="bytes"/> as text: UTF-16 little-endian when it starts with that byte order mark or looks like it (a zero in most
    /// odd places, as ASCII text in UTF-16 has), otherwise UTF-8 (a UTF-8 byte order mark is dropped).
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return Encoding.UTF8.GetString(bytes[3..]);
        return LooksLikeUtf16(bytes) ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// The last <paramref name="count"/> lines of <paramref name="text"/>, joined with "\n", without trailing blank lines or the spaces
    /// at the end of each line; nothing else is changed.
    /// </summary>
    public static string LastLines(string text, int count)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Count - count)));
    }

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
            return false;
        var pairs = bytes.Length / 2;
        var zeros = 0;
        for (var i = 1; i < bytes.Length; i += 2)
        {
            if (bytes[i] == 0)
                zeros++;
        }

        return zeros * 2 > pairs;
    }
}

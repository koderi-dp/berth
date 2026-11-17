using System.Globalization;
using Berth.Engine.Models;

namespace Berth.Presentation;

/// <summary>
/// The RGB a log line's <see cref="AnsiColor"/> is drawn in: the sixteen ANSI colours are the terminal's (<see cref="TerminalTheme.Ansi"/>,
/// light or dark with the app), 16-231 the xterm 6x6x6 cube and 232-255 its greys, computed; a 24-bit colour is used as given.
/// </summary>
public static class LogColors
{
    private static readonly int[] CubeLevels = [0, 95, 135, 175, 215, 255];

    private static readonly int[] DarkTable = Table(dark: true);
    private static readonly int[] LightTable = Table(dark: false);

    /// <summary><paramref name="color"/> as 0xRRGGBB, or null for the default colour (the text's or background's own).</summary>
    public static int? Rgb(AnsiColor color, bool dark) =>
        color.IsDefault ? null
        : color.IsRgb ? color.Rgb
        : (dark ? DarkTable : LightTable)[color.Index];

    private static int[] Table(bool dark)
    {
        var table = new int[256];
        var ansi = TerminalTheme.Ansi(dark);
        for (var i = 0; i < 16; i++)
            table[i] = int.Parse(ansi[i].AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

        for (var i = 16; i < 232; i++)
        {
            var n = i - 16;
            table[i] = (CubeLevels[n / 36] << 16) | (CubeLevels[(n / 6) % 6] << 8) | CubeLevels[n % 6];
        }

        for (var i = 232; i < 256; i++)
        {
            var grey = 8 + ((i - 232) * 10);
            table[i] = (grey << 16) | (grey << 8) | grey;
        }

        return table;
    }
}

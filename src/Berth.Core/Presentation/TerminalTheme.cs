using System.Collections.Immutable;
using System.Globalization;

namespace Berth.Presentation;

/// <summary>The terminal's colours, each "#rrggbb" (lowercase).</summary>
/// <param name="Ansi">The sixteen ANSI colours: black, red, green, yellow, blue, magenta, cyan, white, then their bright forms.</param>
/// <param name="Contrast">The minimum contrast ratio xterm keeps between text and its background.</param>
public sealed record TerminalColors(
    string Background, string Foreground, string Cursor, string Selection, ImmutableArray<string> Ansi, double Contrast);

/// <summary>
/// The terminal's colours. The theme has no tokens for the sixteen ANSI colours, so they are the third place with colour values in
/// code (after the state dots and the chart series), held to a contrast test.
/// </summary>
public static class TerminalTheme
{
    /// <summary>
    /// Windows Terminal's "Campbell", with the four entries that read below 3:1 on the dark card the terminal sits on (#2b2b2b: 5 %
    /// white over #202020) lightened until they pass: red #c50f1f, blue #0037da, magenta #881798 and bright magenta #b4009e. Black
    /// stays: it is the background's colour.
    /// </summary>
    private static readonly ImmutableArray<string> Dark =
    [
        "#0c0c0c", "#d13f4b", "#13a10e", "#c19c00", "#466ee4", "#a854b3", "#3a96dd", "#cccccc",
        "#767676", "#e74856", "#16c60c", "#f9f1a5", "#3b78ff", "#c437b3", "#61d6d6", "#f2f2f2",
    ];

    /// <summary>
    /// Campbell darkened, each entry until it reads at 4.5:1 or more on the light surface (#f3f3f3), so xterm's minimum contrast
    /// leaves them as they are. The three greys cannot all stay pale there, so they are set apart by hand, at least 1.4:1 from each
    /// other, in Campbell's order: bright black the darkest (#3c3c3c), then white (#555555), then bright white (#6e6e6e).
    /// </summary>
    private static readonly ImmutableArray<string> Light =
    [
        "#0c0c0c", "#c50f1f", "#0f810b", "#856c00", "#0037da", "#881798", "#2d73aa", "#555555",
        "#3c3c3c", "#c43d49", "#0e8108", "#736f4c", "#3368de", "#b4009e", "#367878", "#6e6e6e",
    ];

    private const double SelectionOpacity = 0.3;

    /// <param name="dark">Whether the app's theme is dark.</param>
    /// <param name="highContrast">Whether a Windows contrast theme is on: the sixteen collapse to the system pair.</param>
    /// <param name="background">The app's surface colour behind the terminal, "#rrggbb", resolved by the app from the theme.</param>
    /// <param name="foreground">The app's primary text colour.</param>
    public static TerminalColors For(bool dark, bool highContrast, string background, string foreground)
    {
        var back = Parse(background);
        var fore = Parse(foreground);
        var backText = Format(back);
        var foreText = Format(fore);
        var selection = Format(Blend(fore, back, SelectionOpacity));

        if (highContrast)
        {
            var ansi = Enumerable.Range(0, 16).Select(i => i is 0 or 8 ? backText : foreText).ToImmutableArray();
            return new TerminalColors(backText, foreText, foreText, selection, ansi, Contrast: 7);
        }

        return new TerminalColors(backText, foreText, foreText, selection, dark ? Dark : Light, Contrast: 4.5);
    }

    /// <summary>The WCAG contrast ratio of two "#rrggbb" colours, from 1 (the same) to 21 (black on white).</summary>
    public static double ContrastRatio(string a, string b)
    {
        var la = Luminance(Parse(a));
        var lb = Luminance(Parse(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance((int R, int G, int B) c) =>
        (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));

    private static double Linear(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary><paramref name="top"/> at <paramref name="opacity"/> over <paramref name="bottom"/>, as an opaque colour.</summary>
    private static (int R, int G, int B) Blend((int R, int G, int B) top, (int R, int G, int B) bottom, double opacity)
    {
        int Mix(int t, int b) => (int)Math.Round((t * opacity) + (b * (1 - opacity)), MidpointRounding.AwayFromZero);
        return (Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static (int R, int G, int B) Parse(string color)
    {
        ArgumentNullException.ThrowIfNull(color);
        if (color.Length != 7 || color[0] != '#'
            || !int.TryParse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
        {
            throw new ArgumentException("A colour is written \"#rrggbb\".", nameof(color));
        }

        return ((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    private static string Format((int R, int G, int B) c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:x2}{c.G:x2}{c.B:x2}");
}

using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class TerminalThemeTests
{
    private const string LightSurface = "#f3f3f3";

    private const string DarkSurface = "#202020";

    [Fact]
    public void Every_light_colour_reads_on_a_light_background()
    {
        var colors = TerminalTheme.For(dark: false, highContrast: false, LightSurface, "#1a1a1a");

        Assert.Equal(16, colors.Ansi.Length);
        Assert.All(colors.Ansi, c => Assert.True(
            TerminalTheme.ContrastRatio(c, LightSurface) >= 4.5, $"{c} on {LightSurface}: {TerminalTheme.ContrastRatio(c, LightSurface):0.00}"));
        Assert.Equal(4.5, colors.Contrast);
    }

    [Fact]
    public void Lights_three_greys_stay_apart()
    {
        // White, bright black and bright white must reach 4.5:1 on the light surface (or xterm's minimum contrast would darken them
        // into one grey), and differ from each other by at least 1.4:1 so a program that tells them apart still can.
        var colors = TerminalTheme.For(dark: false, highContrast: false, LightSurface, "#1a1a1a");
        int[] greys = [7, 8, 15];

        foreach (var a in greys)
        {
            foreach (var b in greys.Where(b => b > a))
            {
                var ratio = TerminalTheme.ContrastRatio(colors.Ansi[a], colors.Ansi[b]);
                Assert.True(ratio >= 1.4, $"{colors.Ansi[a]} and {colors.Ansi[b]}: {ratio:0.00}");
            }
        }
    }

    [Fact]
    public void Lights_greys_keep_campbells_order_bright_black_darkest_then_white_then_bright_white()
    {
        var colors = TerminalTheme.For(dark: false, highContrast: false, LightSurface, "#1a1a1a");

        Assert.True(TerminalTheme.ContrastRatio(colors.Ansi[8], LightSurface) > TerminalTheme.ContrastRatio(colors.Ansi[7], LightSurface));
        Assert.True(TerminalTheme.ContrastRatio(colors.Ansi[7], LightSurface) > TerminalTheme.ContrastRatio(colors.Ansi[15], LightSurface));
    }

    [Fact]
    public void Every_dark_colour_but_black_reads_on_a_dark_background()
    {
        var colors = TerminalTheme.For(dark: true, highContrast: false, DarkSurface, "#ffffff");

        Assert.Equal(16, colors.Ansi.Length);
        Assert.All(colors.Ansi.Skip(1), c => Assert.True(
            TerminalTheme.ContrastRatio(c, DarkSurface) >= 3, $"{c} on {DarkSurface}: {TerminalTheme.ContrastRatio(c, DarkSurface):0.00}"));
        Assert.Equal(4.5, colors.Contrast);
    }

    [Fact]
    public void Dark_keeps_campbells_colours_that_already_read()
    {
        var colors = TerminalTheme.For(dark: true, highContrast: false, DarkSurface, "#ffffff");

        Assert.Equal("#0c0c0c", colors.Ansi[0]);
        Assert.Equal("#13a10e", colors.Ansi[2]);
        Assert.Equal("#cccccc", colors.Ansi[7]);
        Assert.Equal("#767676", colors.Ansi[8]);
        Assert.Equal("#f2f2f2", colors.Ansi[15]);
    }

    [Fact]
    public void The_surface_and_text_colours_are_the_apps()
    {
        var colors = TerminalTheme.For(dark: true, highContrast: false, "#1C1C1C", "#FFFFFF");

        Assert.Equal("#1c1c1c", colors.Background);
        Assert.Equal("#ffffff", colors.Foreground);
        Assert.Equal("#ffffff", colors.Cursor);
        // 30 % of white over #1c1c1c: 0.3 * 255 + 0.7 * 28 = 96.1.
        Assert.Equal("#606060", colors.Selection);
    }

    [Fact]
    public void High_contrast_uses_the_system_pair()
    {
        var colors = TerminalTheme.For(dark: true, highContrast: true, "#000000", "#FFFF00");

        Assert.Equal("#000000", colors.Background);
        Assert.Equal("#ffff00", colors.Foreground);
        Assert.Equal("#ffff00", colors.Cursor);
        Assert.Equal(7, colors.Contrast);
        for (var i = 0; i < 16; i++)
            Assert.Equal(i is 0 or 8 ? "#000000" : "#ffff00", colors.Ansi[i]);
    }

    [Fact]
    public void Contrast_ratio_of_black_on_white_is_21()
    {
        Assert.Equal(21, TerminalTheme.ContrastRatio("#000000", "#ffffff"), precision: 6);
        Assert.Equal(21, TerminalTheme.ContrastRatio("#FFFFFF", "#000000"), precision: 6);
        Assert.Equal(1, TerminalTheme.ContrastRatio("#777777", "#777777"), precision: 6);
    }

    [Fact]
    public void A_colour_that_is_not_rrggbb_is_refused()
    {
        Assert.Throws<ArgumentException>(() => TerminalTheme.ContrastRatio("red", "#ffffff"));
        Assert.Throws<ArgumentException>(() => TerminalTheme.For(false, false, "#fff", "#000000"));
    }
}

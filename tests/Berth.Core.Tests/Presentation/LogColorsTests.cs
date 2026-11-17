using System.Globalization;
using Berth.Engine.Models;
using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class LogColorsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_default_colour_is_null(bool dark)
    {
        Assert.Null(LogColors.Rgb(AnsiColor.Default, dark));
        Assert.Null(LogColors.Rgb(default, dark));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_sixteen_come_from_the_terminal_theme(bool dark)
    {
        var ansi = TerminalTheme.Ansi(dark);

        for (var i = 0; i < 16; i++)
        {
            var expected = int.Parse(ansi[i].AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            Assert.Equal(expected, LogColors.Rgb(AnsiColor.FromIndex(i), dark));
        }
    }

    [Fact]
    public void Light_and_dark_differ_for_red_and_agree_on_black()
    {
        Assert.Equal(0xc50f1f, LogColors.Rgb(AnsiColor.FromIndex(1), dark: false));
        Assert.Equal(0xd13f4b, LogColors.Rgb(AnsiColor.FromIndex(1), dark: true));
        Assert.Equal(0x0c0c0c, LogColors.Rgb(AnsiColor.FromIndex(0), dark: false));
        Assert.Equal(0x0c0c0c, LogColors.Rgb(AnsiColor.FromIndex(0), dark: true));
    }

    [Theory]
    [InlineData(16, 0x000000)]
    [InlineData(17, 0x00005f)]
    [InlineData(21, 0x0000ff)]
    [InlineData(22, 0x005f00)]
    [InlineData(52, 0x5f0000)]
    [InlineData(67, 0x5f87af)]
    [InlineData(196, 0xff0000)]
    [InlineData(208, 0xff8700)]
    [InlineData(231, 0xffffff)]
    public void The_cube_uses_the_xterm_levels_in_both_themes(int index, int expected)
    {
        Assert.Equal(expected, LogColors.Rgb(AnsiColor.FromIndex(index), dark: true));
        Assert.Equal(expected, LogColors.Rgb(AnsiColor.FromIndex(index), dark: false));
    }

    [Theory]
    [InlineData(232, 0x080808)]
    [InlineData(233, 0x121212)]
    [InlineData(244, 0x808080)]
    [InlineData(255, 0xeeeeee)]
    public void The_greys_step_by_ten_from_eight_in_both_themes(int index, int expected)
    {
        Assert.Equal(expected, LogColors.Rgb(AnsiColor.FromIndex(index), dark: true));
        Assert.Equal(expected, LogColors.Rgb(AnsiColor.FromIndex(index), dark: false));
    }

    [Fact]
    public void A_24_bit_colour_is_passed_through_in_either_theme()
    {
        var colour = AnsiColor.FromRgb(0x12, 0x34, 0x56);

        Assert.Equal(0x123456, LogColors.Rgb(colour, dark: true));
        Assert.Equal(0x123456, LogColors.Rgb(colour, dark: false));
        Assert.Equal(0, LogColors.Rgb(AnsiColor.FromRgb(0, 0, 0), dark: true));
        Assert.Equal(0xffffff, LogColors.Rgb(AnsiColor.FromRgb(255, 255, 255), dark: false));
    }

    [Fact]
    public void Every_palette_index_has_a_colour()
    {
        for (var i = 0; i < 256; i++)
        {
            var rgb = LogColors.Rgb(AnsiColor.FromIndex(i), dark: true);
            Assert.NotNull(rgb);
            Assert.InRange(rgb.Value, 0, 0xffffff);
        }
    }
}

using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class AnsiTextTests
{
    [Fact]
    public void Strips_colour_codes()
    {
        Assert.Equal(
            "INFO ready on port 80",
            AnsiText.Strip("\u001b[1;32mINFO\u001b[0m ready on \u001b[38;5;208mport\u001b[39m 80\u001b[m"));
    }

    [Fact]
    public void Strips_cursor_and_erase_sequences()
    {
        Assert.Equal("progress 50%", AnsiText.Strip("\u001b[2K\u001b[1Gprogress \u001b[?25l50%\u001b[?25h\u001b[A"));
    }

    [Fact]
    public void Strips_osc_title_sequences()
    {
        // Ended by BEL, and by ESC \ (string terminator).
        Assert.Equal("before after end", AnsiText.Strip("before \u001b]0;a window title\u0007after \u001b]2;other\u001b\\end"));
    }

    [Fact]
    public void Keeps_tabs_and_plain_text()
    {
        const string text = "key\tvalue ü 中文 …";

        Assert.Same(text, AnsiText.Strip(text));
        Assert.Equal("a\tb", AnsiText.Strip("a\u0007\t\u0008b\u007f"));
    }

    [Fact]
    public void A_lone_escape_at_the_end_is_dropped()
    {
        Assert.Equal("done", AnsiText.Strip("done\u001b"));
        Assert.Equal("cut", AnsiText.Strip("cut\u001b[1;3"));
    }
}

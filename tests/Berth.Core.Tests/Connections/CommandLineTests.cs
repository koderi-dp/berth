using Berth.Connections;

namespace Berth.Tests.Connections;

public sealed class CommandLineTests
{
    private static void AssertSplit(string text, string program, params string[] arguments)
    {
        var parsed = CommandLine.Parse(text);
        Assert.Null(parsed.Error);
        Assert.Equal(program, parsed.Program);
        Assert.Equal(arguments, parsed.Arguments);
    }

    private static void AssertRefused(string text, string error)
    {
        var parsed = CommandLine.Parse(text);
        Assert.Equal(error, parsed.Error);
        Assert.Equal("", parsed.Program);
        Assert.Empty(parsed.Arguments);
    }

    [Fact]
    public void Splits_on_spaces_and_tabs()
    {
        AssertSplit("wsl.exe -d Ubuntu-24.04 -u root -- systemctl start docker", "wsl.exe", "-d", "Ubuntu-24.04", "-u", "root", "--", "systemctl", "start", "docker");
        AssertSplit("  tool\t a   b  ", "tool", "a", "b");
    }

    [Fact]
    public void Backslashes_in_windows_paths_are_kept()
    {
        AssertSplit(@"C:\tools\fwd.exe --dir C:\data\ \\server\share", @"C:\tools\fwd.exe", "--dir", @"C:\data\", @"\\server\share");
    }

    [Fact]
    public void Quotes_keep_spaces_together()
    {
        AssertSplit("\"C:\\Program Files\\Tool\\tool.exe\" start \"a b\"", @"C:\Program Files\Tool\tool.exe", "start", "a b");
        AssertSplit("tool pre\"fix and\"post", "tool", "prefix andpost");
    }

    [Fact]
    public void A_backslash_before_a_quote_escapes_it_as_the_windows_runtime_does()
    {
        // 2n+1 backslashes then a quote: n backslashes and a literal quote. 2n backslashes then a quote: n backslashes, and the quote delimits.
        AssertSplit("tool \"a \\\"quoted\\\" b\"", "tool", "a \"quoted\" b");
        AssertSplit("tool \"C:\\dir\\\\\"", "tool", @"C:\dir\");
        AssertSplit("tool a\\\\\\\"b", "tool", "a\\\"b");
    }

    [Fact]
    public void Two_quotes_inside_quotes_are_one_quote()
    {
        AssertSplit("tool \"a\"\"b\"", "tool", "a\"b");
    }

    [Fact]
    public void An_empty_quoted_argument_is_kept()
    {
        AssertSplit("tool \"\" x", "tool", "", "x");
    }

    [Fact]
    public void A_blank_line_or_an_empty_program_is_refused()
    {
        foreach (var text in new[] { "", "   ", "\t", "\"\"", "\"\" arg" })
            AssertRefused(text, CommandLine.Blank);
    }

    [Fact]
    public void An_open_quote_is_refused()
    {
        AssertRefused("tool \"open", CommandLine.OpenQuote);
        AssertRefused("\"C:\\Program Files\\tool.exe", CommandLine.OpenQuote);
    }

    [Fact]
    public void A_line_break_is_refused()
    {
        AssertRefused("tool a\nother b", CommandLine.LineBreak);
        AssertRefused("tool a\r", CommandLine.LineBreak);
    }

    [Theory]
    [InlineData(@"C:\tools\start-engine.bat")]
    [InlineData("stop.CMD")]
    [InlineData("\"C:\\My Tools\\up.cmd\" -x")]
    public void A_batch_file_is_refused_because_windows_runs_it_through_cmd(string text)
    {
        AssertRefused(text, CommandLine.BatchFile);
    }

    [Fact]
    public void Cmd_itself_is_allowed_so_a_shell_is_visible_when_one_is_used()
    {
        AssertSplit(@"cmd.exe /c C:\tools\start-engine.bat", "cmd.exe", "/c", @"C:\tools\start-engine.bat");
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("with space")]
    [InlineData("")]
    [InlineData("quote\"inside")]
    [InlineData(@"C:\dir\")]
    [InlineData(@"C:\dir with space\")]
    [InlineData("back\\\\\"slashes")]
    public void Format_gives_text_that_parses_back_to_the_same_words(string argument)
    {
        var text = CommandLine.Format("tool.exe", ["-x", argument]);

        AssertSplit(text, "tool.exe", "-x", argument);
    }

    [Fact]
    public void Format_leaves_plain_words_bare()
    {
        Assert.Equal("wsl.exe -d Ubuntu-24.04 -- systemctl start docker", CommandLine.Format("wsl.exe", ["-d", "Ubuntu-24.04", "--", "systemctl", "start", "docker"]));
        Assert.Equal("\"C:\\Program Files\\t.exe\" \"a b\"", CommandLine.Format(@"C:\Program Files\t.exe", ["a b"]));
    }
}

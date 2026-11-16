using Berth.Text;

namespace Berth.Tests.Text;

/// <summary>The quoting rule <see cref="DockerRunCommand"/> and <see cref="DockerExecCommand"/> share.</summary>
public sealed class ShellQuoteTests
{
    [Theory]
    [InlineData(ShellKind.Posix, "$HOME/bin", "'$HOME/bin'")]
    [InlineData(ShellKind.Posix, "`id`", "'`id`'")]
    [InlineData(ShellKind.Posix, "$(id)", "'$(id)'")]
    [InlineData(ShellKind.Posix, "hi!", "'hi!'")]
    [InlineData(ShellKind.Posix, "say \"hi\"", "'say \"hi\"'")]
    [InlineData(ShellKind.Posix, "it's", @"'it'\''s'")]
    [InlineData(ShellKind.Posix, "it’s", "'it’s'")]
    [InlineData(ShellKind.Posix, "a b", "'a b'")]
    [InlineData(ShellKind.Posix, "a,b", "'a,b'")]
    [InlineData(ShellKind.Posix, "one\ntwo", "'one\ntwo'")]
    [InlineData(ShellKind.Posix, "", "''")]
    [InlineData(ShellKind.Posix, @"C:\data\", @"'C:\data\'")]
    [InlineData(ShellKind.Posix, "@file", "@file")]
    [InlineData(ShellKind.Posix, "$2y$10$abc", "'$2y$10$abc'")]
    [InlineData(ShellKind.PowerShell, "$HOME/bin", "'$HOME/bin'")]
    [InlineData(ShellKind.PowerShell, "`id`", "'`id`'")]
    [InlineData(ShellKind.PowerShell, "$(id)", "'$(id)'")]
    [InlineData(ShellKind.PowerShell, "hi!", "'hi!'")]
    [InlineData(ShellKind.PowerShell, "say \"hi\"", "'say \"hi\"'")]
    [InlineData(ShellKind.PowerShell, "it's", "'it''s'")]
    [InlineData(ShellKind.PowerShell, "it’s", "'it’’s'")]
    [InlineData(ShellKind.PowerShell, "a b", "'a b'")]
    [InlineData(ShellKind.PowerShell, "a,b", "'a,b'")]
    [InlineData(ShellKind.PowerShell, "one\ntwo", "'one\ntwo'")]
    [InlineData(ShellKind.PowerShell, "", "''")]
    [InlineData(ShellKind.PowerShell, @"C:\data\", @"'C:\data\'")]
    [InlineData(ShellKind.PowerShell, "@file", "'@file'")]
    [InlineData(ShellKind.PowerShell, "a@b", "a@b")]
    [InlineData(ShellKind.PowerShell, "$2y$10$abc", "'$2y$10$abc'")]
    public void A_value_with_shell_characters_is_single_quoted_so_the_shell_passes_it_unchanged(ShellKind shell, string value, string expected)
    {
        Assert.Equal(expected, ShellQuote.Quote(value, shell));
    }

    [Theory]
    [InlineData(ShellKind.Posix)]
    [InlineData(ShellKind.PowerShell)]
    public void Safe_characters_are_written_bare(ShellKind shell)
    {
        Assert.Equal("PATH=/usr/local/bin:/usr/bin", ShellQuote.Quote("PATH=/usr/local/bin:/usr/bin", shell));
        Assert.Equal("LEVEL=100%", ShellQuote.Quote("LEVEL=100%", shell));
        Assert.Equal("A_B-c.d+e@f=1", ShellQuote.Quote("A_B-c.d+e@f=1", shell));
    }

    [Theory]
    [InlineData("-c", "'-c'")]
    [InlineData("--%", "'--%'")]
    [InlineData("-a.b", "'-a.b'")]
    [InlineData("--flag=value", "'--flag=value'")]
    [InlineData("-", "'-'")]
    [InlineData("a-b", "a-b")]
    public void PowerShell_quotes_a_value_that_starts_with_a_dash_so_it_is_not_read_as_a_parameter(string value, string expected)
    {
        Assert.Equal(expected, ShellQuote.Quote(value, ShellKind.PowerShell));
    }

    [Fact]
    public void A_posix_shell_leaves_a_value_that_starts_with_a_dash_bare()
    {
        Assert.Equal("--%", ShellQuote.Quote("--%", ShellKind.Posix));
    }
}

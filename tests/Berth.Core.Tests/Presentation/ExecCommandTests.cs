using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class ExecCommandTests
{
    private static void AssertWords(string text, params string[] words)
    {
        var parsed = ExecCommand.Parse(text);
        Assert.Null(parsed.Error);
        Assert.Equal(words, parsed.Command);
    }

    [Fact]
    public void The_choices_are_sh_bash_ash_and_custom_with_sh_first()
    {
        Assert.Equal(["sh", "bash", "ash", "Custom command…"], ExecCommand.Choices.Select(c => c.Label));
        Assert.Equal<string>(["sh"], ExecCommand.Choices[0].Command);
        Assert.Equal<string>(["bash"], ExecCommand.Choices[1].Command);
        Assert.Equal<string>(["ash"], ExecCommand.Choices[2].Command);
        Assert.Empty(ExecCommand.Choices[3].Command);
        Assert.Equal([false, false, false, true], ExecCommand.Choices.Select(c => c.IsCustom));
    }

    [Fact]
    public void Splits_on_whitespace()
    {
        AssertWords("bash", "bash");
        AssertWords("  sh  -c\t ls  ", "sh", "-c", "ls");
    }

    [Fact]
    public void Single_quotes_are_literal()
    {
        AssertWords("sh -c 'echo \"a  b\" \\n $HOME'", "sh", "-c", "echo \"a  b\" \\n $HOME");
        AssertWords("printf ''", "printf", "");
        AssertWords("a'b c'd", "ab cd");
    }

    [Fact]
    public void Double_quotes_allow_escaped_quotes()
    {
        AssertWords("sh -c \"echo \\\"hi\\\" \\\\ \\n\"", "sh", "-c", "echo \"hi\" \\ \\n");
        AssertWords("echo \"it's\"", "echo", "it's");
    }

    [Fact]
    public void A_backslash_keeps_the_next_character()
    {
        AssertWords("echo a\\ b \\'c\\\"", "echo", "a b", "'c\"");
        AssertWords("C:\\\\x", "C:\\x");
    }

    [Fact]
    public void Blank_is_an_error()
    {
        foreach (var text in new[] { "", "   ", "\t" })
        {
            var parsed = ExecCommand.Parse(text);
            Assert.Equal("Enter a command.", parsed.Error);
            Assert.Empty(parsed.Command);
        }
    }

    [Fact]
    public void An_open_quote_is_an_error()
    {
        foreach (var text in new[] { "sh -c 'ls", "echo \"a", "echo \"a\\\"" })
        {
            var parsed = ExecCommand.Parse(text);
            Assert.Equal("Close the quote.", parsed.Error);
            Assert.Empty(parsed.Command);
        }
    }
}

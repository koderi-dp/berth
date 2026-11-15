using Berth.Engine.Models;
using Berth.Presentation;
using static Berth.Tests.Presentation.DetailsFixtures;

namespace Berth.Tests.Presentation;

public sealed class LogSearchTests
{
    [Fact]
    public void Find_gives_the_line_start_and_length_of_each_match()
    {
        var matches = LogSearch.Find([Line("starting"), Line("ready on port 80"), Line("error: port in use")], "port");

        Assert.Equal([new LogMatch(1, 9, 4), new LogMatch(2, 7, 4)], matches);
    }

    [Fact]
    public void Find_reports_several_matches_on_one_line_without_overlap()
    {
        var matches = LogSearch.Find([Line("aaaa ab aa")], "aa");

        Assert.Equal([new LogMatch(0, 0, 2), new LogMatch(0, 2, 2), new LogMatch(0, 8, 2)], matches);
    }

    [Fact]
    public void Find_ignores_case()
    {
        var matches = LogSearch.Find([Line("Error: ERROR error")], "eRRor");

        Assert.Equal([new LogMatch(0, 0, 5), new LogMatch(0, 7, 5), new LogMatch(0, 13, 5)], matches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_term_finds_nothing(string term)
    {
        Assert.Empty(LogSearch.Find([Line("some text"), Line("   ")], term));
    }

    [Fact]
    public void Term_that_is_not_there_finds_nothing()
    {
        Assert.Empty(LogSearch.Find([Line("some text")], "missing"));
    }

    [Fact]
    public void Copy_text_without_timestamps_joins_the_line_texts()
    {
        var text = LogSearch.CopyText(
            [Line("first", T0), Line("second", T0.AddSeconds(1), LogStream.Stderr)],
            timestamps: false);

        Assert.Equal($"first{Environment.NewLine}second", text);
    }

    [Fact]
    public void Copy_text_with_timestamps_puts_each_line_s_utc_time_first()
    {
        var local = new DateTimeOffset(2026, 11, 16, 11, 0, 1, TimeSpan.FromHours(1)).AddTicks(1234567);

        var text = LogSearch.CopyText([Line("first", T0), Line("second", local), Line("no time")], timestamps: true);

        Assert.Equal(
            $"2026-11-16T10:00:00.0000000Z first{Environment.NewLine}"
            + $"2026-11-16T10:00:01.1234567Z second{Environment.NewLine}"
            + "no time",
            text);
    }

    [Fact]
    public void Copy_text_of_no_lines_is_empty()
    {
        Assert.Equal("", LogSearch.CopyText([], timestamps: true));
    }
}

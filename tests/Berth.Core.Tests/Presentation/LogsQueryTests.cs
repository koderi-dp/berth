using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class LogsQueryTests
{
    [Fact]
    public void An_empty_box_matches_everything()
    {
        var query = LogsQuery.Parse("", matchCase: false);

        Assert.True(query.IsEmpty);
        Assert.True(query.IsMatch("anything"));
        Assert.Empty(query.Find("anything"));
    }

    [Fact]
    public void Plain_text_matches_anywhere_ignoring_case_by_default()
    {
        var query = LogsQuery.Parse("Error", matchCase: false);

        Assert.False(query.IsRegex);
        Assert.True(query.IsMatch("an ERROR here"));
        Assert.False(query.IsMatch("all fine"));
        Assert.Equal([(3, 5), (12, 5)], query.Find("an error or error"));
    }

    [Fact]
    public void Plain_text_with_match_case_matches_only_the_same_case()
    {
        var query = LogsQuery.Parse("Error", matchCase: true);

        Assert.False(query.IsMatch("an error"));
        Assert.True(query.IsMatch("an Error"));
    }

    [Fact]
    public void Regex_special_characters_in_plain_text_are_plain()
    {
        var query = LogsQuery.Parse("a.b", matchCase: false);

        Assert.True(query.IsMatch("x a.b y"));
        Assert.False(query.IsMatch("axb"));
    }

    [Fact]
    public void Text_between_slashes_is_a_regular_expression()
    {
        var query = LogsQuery.Parse("/error|warn/", matchCase: false);

        Assert.True(query.IsRegex);
        Assert.True(query.IsMatch("WARN: disk"));
        Assert.True(query.IsMatch("error: x"));
        Assert.False(query.IsMatch("info"));
        Assert.Equal([(0, 4), (9, 5)], query.Find("warn and error"));
    }

    [Fact]
    public void A_regular_expression_follows_match_case()
    {
        Assert.False(LogsQuery.Parse("/Error/", matchCase: true).IsMatch("error"));
        Assert.True(LogsQuery.Parse("/Error/", matchCase: false).IsMatch("error"));
    }

    [Fact]
    public void A_lone_slash_or_an_unclosed_one_is_plain_text_and_empty_slashes_are_no_search()
    {
        Assert.True(LogsQuery.Parse("/", matchCase: false).IsMatch("a/b"));
        Assert.False(LogsQuery.Parse("/err", matchCase: false).IsRegex);
        Assert.True(LogsQuery.Parse("//", matchCase: false).IsEmpty);
    }

    [Fact]
    public void An_invalid_pattern_says_why_and_matches_nothing()
    {
        var query = LogsQuery.Parse("/(unclosed/", matchCase: false);

        Assert.NotNull(query.Error);
        Assert.StartsWith("Not a valid regular expression: ", query.Error, StringComparison.Ordinal);
        Assert.False(query.IsEmpty);
        Assert.False(query.IsMatch("(unclosed"));
        Assert.Empty(query.Find("(unclosed"));
    }

    [Fact]
    public void Empty_matches_mark_nothing()
    {
        Assert.Empty(LogsQuery.Parse("/x*/", matchCase: false).Find("abc"));
    }

    [Fact]
    public void A_pattern_that_runs_too_long_is_stopped_and_matches_nothing()
    {
        // Nested quantifiers on a long run with no match backtrack without end.
        var query = LogsQuery.Parse("/(a+)+$/", matchCase: false);
        var line = new string('a', 40) + "!";

        var kept = query.Filter([line, "aaa"], text => text);

        Assert.Null(kept);
        Assert.Empty(query.Find(line));
    }

    [Fact]
    public void Filter_keeps_the_matching_items_in_order()
    {
        var kept = LogsQuery.Parse("o", matchCase: false).Filter(["one", "two", "three", "four"], text => text);

        Assert.Equal(["one", "two", "four"], kept);
    }
}

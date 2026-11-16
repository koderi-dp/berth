using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using static Berth.Tests.Presentation.DetailsFixtures;

namespace Berth.Tests.Presentation;

public sealed class LogViewTests
{
    [Theory]
    [InlineData(StreamStatus.Idle, false, false, "Loading logs…")]
    [InlineData(StreamStatus.Loading, false, false, "Loading logs…")]
    [InlineData(StreamStatus.Idle, false, true, "No log output yet.")]
    [InlineData(StreamStatus.Loading, false, true, "No log output yet.")]
    [InlineData(StreamStatus.Live, false, false, "No log output yet.")]
    [InlineData(StreamStatus.Live, false, true, "No log output yet.")]
    [InlineData(StreamStatus.Ended, false, false, "The container stopped without any log output.")]
    // Lines were seen and the view was cleared.
    [InlineData(StreamStatus.Live, true, true, "No log output yet.")]
    [InlineData(StreamStatus.Ended, true, true, "Log cleared.")]
    [InlineData(StreamStatus.Ended, true, false, "Log cleared.")]
    public void An_empty_log_says_why(StreamStatus status, bool linesSeen, bool waitedLong, string text)
    {
        Assert.Equal(text, LogView.EmptyText(status, lineCount: 0, linesSeen, waitedLong));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_log_shows_no_empty_text_its_error_says_it(bool linesSeen)
    {
        Assert.Null(LogView.EmptyText(StreamStatus.Failed, lineCount: 0, linesSeen, waitedLong: true));
    }

    [Theory]
    // Open, box focused: Escape clears the text, then a second Escape closes the bar.
    [InlineData(true, true, "port", LogEscape.ClearTerm)]
    [InlineData(true, true, "  ", LogEscape.ClearTerm)]
    [InlineData(true, true, "", LogEscape.CloseSearch)]
    // The focus is elsewhere, or the bar is closed: Escape is the page's (back to the list).
    [InlineData(true, false, "port", LogEscape.Leave)]
    [InlineData(true, false, "", LogEscape.Leave)]
    [InlineData(false, false, "", LogEscape.Leave)]
    public void Escape_clears_the_search_box_then_closes_the_search_and_otherwise_leaves(bool open, bool focused, string term, LogEscape action)
    {
        Assert.Equal(action, LogView.Escape(new LogSearchState(open, term), focused));
    }

    [Fact]
    public void The_search_opens_empty_and_keeps_its_term_until_it_is_closed()
    {
        var opened = LogSearchState.Closed.Opened();
        Assert.Equal(new LogSearchState(Open: true, Term: ""), opened);

        var typed = opened.WithTerm("alert");
        Assert.Equal(new LogSearchState(Open: true, Term: "alert"), typed);

        // Opening an open search (a repeated Ctrl+F, the toggle reporting On again) keeps the term.
        Assert.Same(typed, typed.Opened());
        Assert.Equal(new LogSearchState(Open: true, Term: ""), typed.Cleared());
        Assert.Equal(new LogSearchState(Open: false, Term: ""), LogSearchState.Closed);
    }

    [Fact]
    public void A_term_arriving_while_the_search_is_closed_opens_nothing()
    {
        // A box being torn down can still report its text; the closed search stays closed and empty.
        Assert.Same(LogSearchState.Closed, LogSearchState.Closed.WithTerm("late"));
    }

    [Fact]
    public void Setting_the_same_term_changes_nothing()
    {
        var typed = LogSearchState.Closed.Opened().WithTerm("alert");
        Assert.Same(typed, typed.WithTerm("alert"));
    }

    [Theory]
    [InlineData(StreamStatus.Loading)]
    [InlineData(StreamStatus.Live)]
    [InlineData(StreamStatus.Ended)]
    [InlineData(StreamStatus.Failed)]
    public void A_log_with_lines_shows_them_instead_of_an_empty_text(StreamStatus status)
    {
        Assert.Null(LogView.EmptyText(status, lineCount: 3, linesSeen: true, waitedLong: true));
    }

    [Fact]
    public void A_stopped_container_s_lines_say_they_are_its_last()
    {
        Assert.Equal(["The container stopped. These are its last lines."], LogView.Notes(StreamStatus.Ended, lineCount: 3, dropped: 0));
    }

    [Fact]
    public void Dropped_lines_say_only_the_latest_are_shown()
    {
        Assert.Equal(["Showing the latest 10,000 lines."], LogView.Notes(StreamStatus.Live, lineCount: 10_000, dropped: 1));
        Assert.Equal(
            ["The container stopped. These are its last lines.", "Showing the latest 10,000 lines."],
            LogView.Notes(StreamStatus.Ended, lineCount: 10_000, dropped: 5));
    }

    [Theory]
    [InlineData(StreamStatus.Loading)]
    [InlineData(StreamStatus.Live)]
    [InlineData(StreamStatus.Failed)]
    public void A_log_that_dropped_nothing_and_did_not_end_has_no_notes(StreamStatus status)
    {
        Assert.Empty(LogView.Notes(status, lineCount: 3, dropped: 0));
    }

    [Fact]
    public void An_empty_ended_log_has_no_notes_its_empty_text_says_it()
    {
        Assert.Empty(LogView.Notes(StreamStatus.Ended, lineCount: 0, dropped: 0));
    }

    [Theory]
    [InlineData("", 0, -1, "")]
    [InlineData("  ", 0, -1, "")]
    [InlineData("port", 0, -1, "No matches")]
    [InlineData("port", 17, 2, "3 of 17")]
    [InlineData("port", 1, 0, "1 of 1")]
    [InlineData("port", 12_345, 1_233, "1,234 of 12,345")]
    public void The_counter_says_which_match_of_how_many(string term, int count, int current, string text)
    {
        Assert.Equal(text, LogView.Counter(term, current, count));
    }

    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(1, 3, 2)]
    [InlineData(2, 3, 0)]
    [InlineData(-1, 3, 0)]
    [InlineData(-1, 0, -1)]
    public void Next_goes_to_the_following_match_and_wraps_to_the_first(int current, int count, int next)
    {
        Assert.Equal(next, LogView.Next(current, count));
    }

    [Theory]
    [InlineData(2, 3, 1)]
    [InlineData(0, 3, 2)]
    [InlineData(-1, 3, 2)]
    [InlineData(-1, 0, -1)]
    public void Previous_goes_to_the_one_before_and_wraps_to_the_last(int current, int count, int previous)
    {
        Assert.Equal(previous, LogView.Previous(current, count));
    }

    [Fact]
    public void Without_a_chosen_match_the_newest_is_current()
    {
        LogMatch[] matches = [new(0, 0, 4), new(2, 1, 4), new(5, 0, 4)];

        Assert.Equal(2, LogView.CurrentIndex(matches, firstNumber: 1, anchor: null));
        Assert.Equal(-1, LogView.CurrentIndex([], firstNumber: 1, anchor: null));
    }

    [Fact]
    public void The_chosen_match_stays_current_while_older_lines_are_dropped()
    {
        // Lines 11..20 are held; the match chosen on line 13 is at index 2 now.
        LogMatch[] before = [new(0, 0, 4), new(2, 3, 4), new(2, 9, 4), new(6, 0, 4)];
        var anchor = LogView.Anchor(before[2], firstNumber: 11);

        // Two lines dropped: line 13 is index 0 now.
        LogMatch[] after = [new(0, 3, 4), new(0, 9, 4), new(4, 0, 4), new(9, 1, 4)];

        Assert.Equal(1, LogView.CurrentIndex(after, firstNumber: 13, anchor));
    }

    [Fact]
    public void A_chosen_match_that_was_dropped_gives_way_to_the_oldest_one_left()
    {
        var anchor = LogView.Anchor(new LogMatch(0, 3, 4), firstNumber: 11);
        LogMatch[] after = [new(0, 0, 4), new(3, 1, 4)];

        Assert.Equal(0, LogView.CurrentIndex(after, firstNumber: 20, anchor));
    }

    [Fact]
    public void A_chosen_match_that_no_longer_matches_gives_way_to_the_next_one()
    {
        var anchor = LogView.Anchor(new LogMatch(2, 5, 4), firstNumber: 1);
        LogMatch[] after = [new(0, 0, 4), new(2, 1, 4), new(2, 8, 4), new(4, 0, 4)];

        Assert.Equal(2, LogView.CurrentIndex(after, firstNumber: 1, anchor));
    }

    [Fact]
    public void A_chosen_match_after_every_match_left_gives_way_to_the_newest()
    {
        var anchor = LogView.Anchor(new LogMatch(9, 0, 4), firstNumber: 1);
        LogMatch[] after = [new(0, 0, 4), new(2, 1, 4)];

        Assert.Equal(1, LogView.CurrentIndex(after, firstNumber: 1, anchor));
    }

    [Fact]
    public void The_matches_of_a_line_are_found_among_all_matches()
    {
        LogMatch[] matches = [new(0, 0, 4), new(2, 1, 4), new(2, 8, 4), new(4, 0, 4)];

        Assert.Equal((1, 2), LogView.MatchesOnLine(matches, 2));
        Assert.Equal((3, 1), LogView.MatchesOnLine(matches, 4));
        Assert.Equal(0, LogView.MatchesOnLine(matches, 1).Count);
        Assert.Equal(0, LogView.MatchesOnLine(matches, 7).Count);
        Assert.Equal(0, LogView.MatchesOnLine([], 0).Count);
    }

    [Theory]
    [InlineData(1000, 1000, true)]
    [InlineData(996, 1000, true)]
    [InlineData(990, 1000, false)]
    [InlineData(0, 0, true)]
    [InlineData(0, 400, false)]
    public void The_view_follows_while_it_is_at_the_bottom(double offset, double scrollable, bool follows)
    {
        Assert.Equal(follows, LogView.IsAtBottom(offset, scrollable));
    }

    [Theory]
    // Following: new lines grow the log below the view, which does not move it; only scrolling up stops following.
    [InlineData(true, 500, 500, 1000, true)]
    [InlineData(true, 500, 700, 1000, true)]
    [InlineData(true, 500, 480, 1000, false)]
    // Not following: only reaching the bottom starts it again.
    [InlineData(false, 500, 700, 1000, false)]
    [InlineData(false, 500, 996, 1000, true)]
    // The log got shorter (cleared, lines unwrapped): the view is pulled up to the new bottom, which is still the bottom.
    [InlineData(true, 900, 100, 100, true)]
    [InlineData(false, 500, 0, 0, true)]
    public void Following_stops_when_the_view_is_scrolled_up_and_starts_again_at_the_bottom(
        bool following, double previousOffset, double offset, double scrollable, bool follows)
    {
        Assert.Equal(follows, LogView.Follows(following, previousOffset, offset, scrollable));
    }

    [Fact]
    public void A_line_shows_its_utc_timestamp_first_when_timestamps_are_on()
    {
        var line = Line("ready", T0.AddTicks(1234567), LogStream.Stderr);

        Assert.Equal("ready", LogView.LineText(line, timestamps: false));
        Assert.Equal("2026-11-16T10:00:00.1234567Z ready", LogView.LineText(line, timestamps: true));
        Assert.Equal("no time", LogView.LineText(Line("no time"), timestamps: true));
    }

    [Fact]
    public void The_text_offset_of_a_line_is_the_length_of_its_timestamp()
    {
        Assert.Equal(0, LogView.TextOffset(Line("ready", T0), timestamps: false));
        Assert.Equal(29, LogView.TextOffset(Line("ready", T0), timestamps: true));
        Assert.Equal(0, LogView.TextOffset(Line("ready"), timestamps: true));
    }
}

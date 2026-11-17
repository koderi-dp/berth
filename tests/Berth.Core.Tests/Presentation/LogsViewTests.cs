using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using Berth.Tests.Session;

namespace Berth.Tests.Presentation;

public sealed class LogsViewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 21, 21, 123, TimeSpan.Zero);

    /// <summary>A logs state with running "web" (project shop, service web), running "db" and a stopped "old" that wrote lines.</summary>
    private static LogsState State()
    {
        var (state, _) = LogsReducer.Reduce(
            LogsState.Initial,
            new LogsContainersChanged(
            [
                LogsReducerTests.Container("web", project: "shop", service: "web"),
                LogsReducerTests.Container("db"),
                LogsReducerTests.Container("old"),
            ]));
        IncomingLogLine Line(string id, string text, int second, LogStream stream = LogStream.Stdout) =>
            new(id, state.Sources[id].Generation, new LogLine(stream, T0.AddSeconds(second), text));

        state = LogsReducer.Reduce(
            state,
            new LogsLinesArrived(
            [
                Line("web", "GET / 200", 0),
                Line("db", "ready to accept connections", 1),
                Line("web", "error: upstream timed out", 2, LogStream.Stderr),
                Line("old", "shutting down", 3),
            ],
            T0)).State;
        state = LogsReducer.Reduce(state, new LogsStreamEnded("old", state.Sources["old"].Generation, Error: null)).State;
        return LogsReducer.Reduce(
            state,
            new LogsContainersChanged(
            [
                LogsReducerTests.Container("web", project: "shop", service: "web"),
                LogsReducerTests.Container("db"),
                LogsReducerTests.Container("old", state: "exited"),
            ])).State;
    }

    private static IEnumerable<string> Texts(LogsShown shown) => shown.Entries.Select(e => e.Line.Text);

    [Fact]
    public void Without_filter_or_search_every_line_shows_and_the_log_is_not_copied()
    {
        var state = State();

        var shown = LogsView.Shown(state, LogsFilter.All, LogsQuery.None);

        Assert.Same(state.Log.Entries, shown.Entries);
        Assert.Equal(4, shown.Total);
        Assert.Null(shown.QueryError);
    }

    [Fact]
    public void The_stream_filter_keeps_only_the_streams_chosen()
    {
        var shown = LogsView.Shown(State(), LogsFilter.All with { Stdout = false }, LogsQuery.None);

        Assert.Equal(["error: upstream timed out"], Texts(shown));
    }

    [Fact]
    public void Running_only_hides_the_lines_of_stopped_containers()
    {
        var shown = LogsView.Shown(State(), LogsFilter.All with { RunningOnly = true }, LogsQuery.None);

        Assert.DoesNotContain("shutting down", Texts(shown));
        Assert.Equal(3, shown.Entries.Count);
    }

    [Fact]
    public void The_object_filter_keeps_a_compose_project_or_a_container()
    {
        var state = State();
        var all = LogsView.Objects(state).Select(o => o.Key).ToArray();

        var onlyShop = LogsFilter.All with { Objects = ["project:shop"] };
        Assert.Equal(["GET / 200", "error: upstream timed out"], Texts(LogsView.Shown(state, onlyShop, LogsQuery.None)));

        var withoutDb = LogsFilter.All.WithObject("container:db", shown: false, all);
        Assert.DoesNotContain("ready to accept connections", Texts(LogsView.Shown(state, withoutDb, LogsQuery.None)));
        Assert.True(withoutDb.IsActive);

        // Checking it again shows everything: the filter is "all" again.
        Assert.Null(withoutDb.WithObject("container:db", shown: true, all).Objects);
    }

    [Fact]
    public void The_filter_choices_are_the_projects_and_the_containers_outside_one_by_name()
    {
        var objects = LogsView.Objects(State());

        Assert.Equal(
            [new LogObject("container:db", "db-name", false), new LogObject("container:old", "old-name", false), new LogObject("project:shop", "shop", true)],
            objects.ToArray());
    }

    [Fact]
    public void Search_and_filter_combine()
    {
        var shown = LogsView.Shown(State(), LogsFilter.All with { Objects = ["project:shop"] }, LogsQuery.Parse("/time[sd]/", matchCase: false));

        Assert.Equal(["error: upstream timed out"], Texts(shown));
        Assert.Equal(4, shown.Total);
    }

    [Fact]
    public void An_invalid_pattern_shows_nothing_and_says_why()
    {
        var shown = LogsView.Shown(State(), LogsFilter.All, LogsQuery.Parse("/[/", matchCase: false));

        Assert.Empty(shown.Entries);
        Assert.StartsWith("Not a valid regular expression", shown.QueryError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(274, 274, false, "274 lines")]
    [InlineData(41, 274, true, "41 of 274 lines")]
    [InlineData(1, 1, false, "1 line")]
    [InlineData(0, 1, true, "0 of 1 line")]
    [InlineData(1200, 20000, true, "1,200 of 20,000 lines")]
    public void The_count_line_says_how_many_lines_show(int shown, int total, bool narrowed, string expected) =>
        Assert.Equal(expected, LogsView.CountText(shown, total, narrowed));

    [Fact]
    public void The_empty_texts_say_why_nothing_shows()
    {
        Assert.Equal("Loading logs…", LogsView.EmptyText(LogsState.Initial, 0, waiting: false));
        Assert.Equal("Waiting for the engine…", LogsView.EmptyText(LogsState.Initial, 0, waiting: true));

        var none = LogsReducer.Reduce(LogsState.Initial, new LogsContainersChanged([])).State;
        Assert.Equal("There are no containers on this engine.", LogsView.EmptyText(none, 0, waiting: false));

        var stopped = LogsReducer.Reduce(LogsState.Initial, new LogsContainersChanged([LogsReducerTests.Container("a", state: "exited")])).State;
        Assert.Equal("No container is running. Logs show here when one starts.", LogsView.EmptyText(stopped, 0, waiting: false));

        var quiet = LogsReducer.Reduce(LogsState.Initial, new LogsContainersChanged([LogsReducerTests.Container("a")])).State;
        Assert.Equal("No log lines yet.", LogsView.EmptyText(quiet, 0, waiting: false));

        Assert.Equal("No lines match the search or filter.", LogsView.EmptyText(State(), 0, waiting: false));
        Assert.Null(LogsView.EmptyText(State(), 3, waiting: false));
    }

    [Fact]
    public void The_notes_say_when_lines_were_dropped_and_logs_are_not_followed()
    {
        var containers = Enumerable.Range(0, 53).Select(i => LogsReducerTests.Container($"c{i}", created: i)).ToArray();
        var state = LogsReducer.Reduce(LogsState.Initial, new LogsContainersChanged([.. containers])).State;

        Assert.Equal(["3 running containers are not followed: at most 50 logs are followed at once."], LogsView.Notes(state, waiting: false));
        Assert.Empty(LogsView.Notes(State(), waiting: false));
        Assert.Equal(["Waiting for the engine…"], LogsView.Notes(State(), waiting: true));
    }

    [Fact]
    public void Times_show_local_to_the_second_and_to_the_millisecond_when_expanded()
    {
        var entry = State().Log.Entries[0];
        var local = T0.ToLocalTime();

        Assert.Equal(local.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), LogsView.Time(entry));
        Assert.Equal(local.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), LogsView.FullTime(entry));
        Assert.EndsWith(".123", LogsView.FullTime(entry), StringComparison.Ordinal);
    }

    [Fact]
    public void Copied_text_has_the_columns_shown()
    {
        var entries = State().Log.Entries.Take(2).ToList();

        var full = LogsView.CopyText(entries, timestamps: true, objects: true).Split(Environment.NewLine);
        var plain = LogsView.CopyText(entries, timestamps: false, objects: false);

        Assert.Equal($"{LogsView.FullTime(entries[0])} shop/web GET / 200", full[0]);
        Assert.Equal($"GET / 200{Environment.NewLine}ready to accept connections", plain);
    }

    [Fact]
    public void Each_container_keeps_the_same_colour_and_the_colours_spread()
    {
        Assert.Equal(LogsPalette.Index("4f1c2a"), LogsPalette.Index("4f1c2a"));

        var used = Enumerable.Range(0, 200).Select(i => LogsPalette.Index($"container-{i}")).ToHashSet();
        Assert.Equal(LogsPalette.Count, used.Count);
        Assert.All(used, i => Assert.InRange(i, 0, LogsPalette.Count - 1));
    }

    [Fact]
    public void Every_colour_reads_on_its_theme_background()
    {
        for (var i = 0; i < LogsPalette.Count; i++)
        {
            Assert.True(Contrast(LogsPalette.Rgb(i, dark: false), 0xF3F3F3) >= 4.5, $"light colour {i}");
            Assert.True(Contrast(LogsPalette.Rgb(i, dark: true), 0x202020) >= 4.5, $"dark colour {i}");
        }
    }

    private static double Contrast(int a, int b)
    {
        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(int rgb) =>
            (0.2126 * Channel((rgb >> 16) & 0xFF)) + (0.7152 * Channel((rgb >> 8) & 0xFF)) + (0.0722 * Channel(rgb & 0xFF));

        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [InlineData(true, false, true, false, false, LogsSessionStep.Open)]
    [InlineData(true, false, true, true, false, LogsSessionStep.Keep)]
    [InlineData(true, false, true, true, true, LogsSessionStep.Open)]
    [InlineData(true, false, false, true, true, LogsSessionStep.Keep)]
    [InlineData(true, false, false, false, false, LogsSessionStep.Keep)]
    [InlineData(false, false, true, true, false, LogsSessionStep.CloseLater)]
    [InlineData(false, false, true, false, false, LogsSessionStep.Keep)]
    [InlineData(true, true, true, true, false, LogsSessionStep.Close)]
    [InlineData(false, true, true, true, false, LogsSessionStep.Close)]
    [InlineData(false, true, true, false, false, LogsSessionStep.Keep)]
    public void The_session_runs_while_the_page_shows_a_while_after_and_never_while_hidden(
        bool shown, bool hidden, bool connected, bool open, bool cutOff, LogsSessionStep expected) =>
        Assert.Equal(expected, LogsLifecycle.Next(shown, hidden, connected, open, cutOff));

    [Fact]
    public void Back_from_a_container_opened_on_the_logs_page_goes_to_logs() =>
        Assert.Equal("Back to logs", BackTarget.Logs.Label);

    [Fact]
    public void The_suggested_file_name_is_dated()
    {
        var name = LogsView.SaveName(T0);

        Assert.StartsWith("logs-", name, StringComparison.Ordinal);
        Assert.Matches(@"^logs-\d{8}-\d{6}$", name);
    }

    [Fact]
    public void The_stream_names_are_the_engine_words() =>
        Assert.Equal(["stdout", "stderr"], new[] { LogStream.Stdout, LogStream.Stderr }.Select(LogsView.StreamName));

    [Fact]
    public void A_filter_with_nothing_unchecked_is_not_active()
    {
        Assert.False(LogsFilter.All.IsActive);
        Assert.True((LogsFilter.All with { Objects = ImmutableHashSet<string>.Empty }).IsActive);
    }
}

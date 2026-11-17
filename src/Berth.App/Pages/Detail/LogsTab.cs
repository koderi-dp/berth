using System.Collections;
using System.Globalization;
using Berth.Components;
using Berth.Engine.Models;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Berth.Settings;
using Berth.State;
using Microsoft.UI.Input;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using static Microsoft.UI.Reactor.Factories;
using Run = Microsoft.UI.Xaml.Documents.Run;
using TextHighlighter = Microsoft.UI.Xaml.Documents.TextHighlighter;
using TextRange = Microsoft.UI.Xaml.Documents.TextRange;

namespace Berth.Pages.Detail;

/// <summary>What the Logs tab is given.</summary>
/// <param name="Detail">The container's detail state: its log, and where the log stream stands.</param>
/// <param name="Waiting">
/// The page waits for the engine (see <see cref="DetailPage.Waiting"/>): the log says so in place of its stream's state, with no Retry.
/// </param>
/// <param name="ClearLogs">Empties the view (the container's log is kept).</param>
/// <param name="Search">
/// The search, kept by the page so it outlives the tab being mounted again (another tab chosen and back).
/// </param>
/// <param name="UpdateSearch">Changes <paramref name="Search"/>.</param>
/// <param name="Keys">
/// What the tab does with the page's Ctrl+F and Escape, filled in while the tab is shown (the page owns the shortcuts).
/// </param>
/// <param name="SearchRequests">
/// Counts the page's Ctrl+F presses: each one, also one that chose this tab, opens the search with the focus in its box.
/// </param>
internal sealed record LogsTabProps(
    DetailState Detail,
    bool Waiting,
    Action ClearLogs,
    LogSearchState Search,
    Action<Func<LogSearchState, LogSearchState>> UpdateSearch,
    LogsTabKeys Keys,
    int SearchRequests);

/// <summary>
/// What the Logs tab does with its page's shortcuts, kept by the page (one instance for the page's life) and filled in by the tab
/// while it is shown.
/// </summary>
internal sealed class LogsTabKeys
{
    /// <summary>
    /// Escape, while it is the search's (<see cref="LogView.Escape"/>): empties the box, or closes the search. Null while Escape is
    /// the page's (back to the list) or the tab is not shown.
    /// </summary>
    public Action? Escape { get; set; }

    /// <summary>Set by the page on Ctrl+F: the tab, once it renders, puts the focus in the search box and clears it.</summary>
    public bool FocusSearch { get; set; }
}

/// <summary>
/// The "Logs" tab of a container's detail page: the container's log, followed live, in a virtualized list that fills the tab, with a
/// narrow toolbar at its right edge (search, timestamps, wrap, colours, copy all, clear) and a button back to the latest line while scrolled
/// up. What it shows is decided in <see cref="LogView"/>; this holds only view state (the search, whether it follows).
/// </summary>
internal sealed class LogsTab : Component<LogsTabProps>
{
    private const double LineFontSize = 12;
    private const double LineHeight = 20;
    private const double ToolbarButtonSize = 36;

    private static readonly GridSize[] Columns = [GridSize.Star(), GridSize.Auto];
    private static readonly GridSize[] OneRow = [GridSize.Star()];
    private static readonly GridSize[] MainColumns = [GridSize.Star()];

    // The search, the failure, the notes, the log, the footer.
    private static readonly GridSize[] MainRows = [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto];


    public override Element Render()
    {
        var detail = Props.Detail;
        var logs = detail.Logs;
        var options = UseExternalStore(SettingsHook.Subscribe, SettingsHook.Snapshot).Logs;
        var dark = UseIsDarkTheme();
        var highContrast = UseHighContrast();
        var search = Props.Search;
        var updateSearch = Props.UpdateSearch;
        var searchOpen = search.Open;
        var term = search.Term;
        var (anchor, setAnchor) = UseState<LogMatchAnchor?>(null);
        var (focusRequests, updateFocusRequests) = UseReducer(0);
        var (searchFocused, setSearchFocused) = UseState(false);
        var (following, setFollowing) = UseState(true);
        var (waitedGeneration, setWaitedGeneration) = UseState(-1);
        var searchBox = this.UseElementRef<TextBox>();
        var scroller = UseMemo(() => new LogScroller(), Array.Empty<object>());
        scroller.OnFollowingChanged = setFollowing;

        var rows = UseMemo(() => new LogRows(logs), logs);
        var matches = UseMemo(() => LogSearch.Find(logs.Lines, term), logs, term);
        var current = LogView.CurrentIndex(matches, logs.FirstNumber, anchor);

        // "Loading logs…" only for the first moments of a stream: a followed log says nothing until its first line.
        var generation = detail.StreamGeneration;
        UseEffect(
            () =>
            {
                var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
                timer.Interval = LogView.LoadingTextTime;
                timer.IsRepeating = false;
                timer.Tick += (_, _) => setWaitedGeneration(generation);
                timer.Start();
                return timer.Stop;
            },
            generation);

        // While following, the newest line stays in view as lines arrive and when wrapping or timestamps change their height.
        UseEffect(() => scroller.KeepAtBottom(), logs, options, searchOpen);

        // Ctrl+F (or the toolbar's Search) opens the search and puts the focus in its box.
        UseEffect(
            () =>
            {
                if (focusRequests > 0 && searchBox.Current is { } box)
                {
                    box.Focus(FocusState.Keyboard);
                    box.SelectAll();
                }
            },
            focusRequests);

        // A new term shows its newest match, and the chosen match starts over from there.
        UseEffect(
            () =>
            {
                setAnchor(null);
                if (matches.Count > 0)
                    scroller.BringIntoView(matches[^1].Line);
            },
            term);

        // Escape in the search box empties it, then closes the search; anywhere else it is the page's (back to the list). The page
        // owns the key and asks the tab (Props.Keys) whether it is the search's.
        var escape = LogView.Escape(search, searchFocused);
        var keys = Props.Keys;

        void OpenSearch()
        {
            updateSearch(s => s.Opened());
            updateFocusRequests(n => n + 1);
        }

        void CloseSearch() => updateSearch(_ => LogSearchState.Closed);

        // The box reports every change of its text here; the term follows it and nothing else writes the term while the box is shown,
        // so the two cannot disagree.
        void ChangeTerm(string value) => updateSearch(s => s.WithTerm(value));

        // Empties the box itself (which reports "" back), so the box and the term stay the same.
        void ClearTerm()
        {
            if (searchBox.Current is { } box)
                box.Text = "";
            updateSearch(s => s.Cleared());
        }

        void OnEscape()
        {
            if (escape == LogEscape.ClearTerm)
                ClearTerm();
            else if (escape == LogEscape.CloseSearch)
                CloseSearch();
        }

        UseEffect(
            () =>
            {
                keys.Escape = escape == LogEscape.Leave ? null : OnEscape;
                return () => keys.Escape = null;
            },
            escape);

        // Ctrl+F on the page (which opened the search, choosing this tab if another one showed): the box takes the focus once it is
        // there. Runs when the tab is shown too, for a press that chose it.
        UseEffect(
            () =>
            {
                if (keys.FocusSearch)
                {
                    keys.FocusSearch = false;
                    updateFocusRequests(n => n + 1);
                }
            },
            Props.SearchRequests);

        void Step(int index)
        {
            if (index < 0)
                return;
            setAnchor(LogView.Anchor(matches[index], logs.FirstNumber));
            scroller.BringIntoView(matches[index].Line);
        }

        void Next() => Step(LogView.Next(current, matches.Count));
        void Previous() => Step(LogView.Previous(current, matches.Count));

        void Clear()
        {
            Props.ClearLogs();
            scroller.Follow();
        }

        var waiting = Props.Waiting;
        var emptyText = LogView.EmptyText(detail.LogStatus, logs.Lines.Count, linesSeen: logs.FirstNumber > 1, waitedGeneration == generation, waiting);
        var notes = LogView.Notes(detail.LogStatus, logs.Lines.Count, logs.Dropped, waiting);
        var log = emptyText is null
            ? LogList(rows, options, colors: options.Colors && !highContrast, matches, current, dark, scroller)
            : Widgets.CenteredNote(emptyText).AutomationId("log-empty");

        var latest = following || emptyText is not null
            ? null
            : Button(Widgets.Glyph(Glyphs.ArrowDown), scroller.Follow)
                .AutomationId("log-latest")
                .AutomationName("Jump to the latest line")
                .ToolTip("Jump to the latest line")
                .Size(ToolbarButtonSize, ToolbarButtonSize)
                .Padding(0)
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Bottom)
                .Margin(0, 0, 12, 12);

        // A grid, not a flex column: layout runs on every batch of lines, and a flex column also measures the list for its minimum size.
        var main = Grid(
                MainColumns,
                MainRows,
                searchOpen
                    ? SearchBar(term, ChangeTerm, LogView.Counter(term, current, matches.Count), matches.Count > 0, Next, Previous, CloseSearch, searchBox, setSearchFocused)
                        .Margin(0, 0, 0, 8)
                        .Grid(row: 0)
                    : null,
                FailureBar(detail, waiting)?.Margin(0, 0, 0, 8).Grid(row: 1),
                notes.Count == 0
                    ? null
                    : Caption(string.Join("  ", notes)).Foreground(Theme.SecondaryText).AutomationId("log-notes").Margin(0, 0, 0, 8).Grid(row: 2),
                Grid(MainColumns, OneRow, log, latest).Grid(row: 3),
                Footer(following, scroller).Grid(row: 4))
            .Grid(column: 0);

        // The toolbar's Search follows the value its toggle reports, so a repeated report never flips the search the wrong way.
        void SetSearchShown(bool shown)
        {
            if (shown)
                OpenSearch();
            else
                CloseSearch();
        }

        var toolbar = Toolbar(options, logs, SetSearchShown, searchOpen, Clear).Grid(column: 1);

        return Grid(Columns, OneRow, main, toolbar).Margin(0, 0, 0, 16).Flex(grow: 1, basis: 0);
    }

    /// <summary>
    /// The log, one line per row, virtualized: only the rows in view are built, each from its line alone. In colour when
    /// <paramref name="colors"/> (the toolbar's Colours, never under a Windows contrast theme).
    /// </summary>
    private static Element LogList(
        LogRows rows, LogViewOptions options, bool colors, IReadOnlyList<LogMatch> matches, int current, bool dark, LogScroller scroller) =>
        LazyVStack(rows, row => row.Position.ToString(CultureInfo.InvariantCulture), (row, index) => Line(row.Line, index, options, colors, matches, current, dark))
            .Set(view =>
            {
                LogScroller.Configure(view, options.Wrap);
                scroller.Attach(view);
            })
            .SetRepeater(repeater =>
            {
                scroller.Repeater = repeater;
                if (repeater.Layout is StackLayout stack)
                    stack.Spacing = 0;
                repeater.HorizontalAlignment = options.Wrap ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            })
            .AutomationId("log-lines")
            .AutomationName("Log");

    /// <summary>
    /// One line: monospace, selectable, stderr in the caution colour, with this line's search matches marked (the current one in the
    /// accent colour). Every line sets its marks, also to none, so a recycled row does not keep the marks of the line it showed before.
    /// A line with colour codes, while <paramref name="colors"/>, is drawn in coloured runs (see <see cref="Paint"/>).
    /// </summary>
    private static Element Line(LogLine line, int index, LogViewOptions options, bool colors, IReadOnlyList<LogMatch> matches, int current, bool dark)
    {
        var (first, count) = LogView.MatchesOnLine(matches, index);
        var offset = count == 0 ? 0 : LogView.TextOffset(line, options.Timestamps);
        var currentOnLine = current >= first && current < first + count ? current : -1;
        var look = new LogText.LineLook(line, options.Timestamps, colors && !line.Spans.IsEmpty);

        return (TextBlock(LogView.LineText(line, options.Timestamps)) with { LineHeight = LineHeight })
            .FontFamily(LogText.Mono)
            .FontSize(LineFontSize)
            .TextWrapping(options.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap)
            .IsTextSelectionEnabled()
            .Foreground(line.Stream == LogStream.Stderr ? Theme.SystemCaution : Theme.PrimaryText)
            .Padding(12, 0, 12, 0)
            .Set(block => LogText.Mark(block, look, matches, first, count, offset, currentOnLine, dark));
    }


    /// <summary>
    /// Under the log: the "Stick to bottom" switch, on while the view follows new lines. Scrolling up turns it off; turning it on, the
    /// jump button or scrolling to the end turns it back on.
    /// </summary>
    internal static Element Footer(bool following, LogScroller scroller) =>
        ToggleSwitch(following, on =>
            {
                if (on)
                    scroller.Follow();
                else
                    scroller.StopFollowing();
            }, onContent: "Stick to bottom", offContent: "Stick to bottom")
            .AutomationId("log-stick")
            .AutomationName("Stick to bottom")
            .ToolTip("Keep the newest line in view as lines arrive")
            .HAlign(HorizontalAlignment.Right)
            .Margin(0, 4, 0, 0);

    /// <summary>The search: its box, "3 of 17", previous and next (Shift+Enter and Enter), and close (Esc).</summary>
    private static Element SearchBar(
        string term,
        Action<string> setTerm,
        string counter,
        bool hasMatches,
        Action next,
        Action previous,
        Action close,
        ElementRef<TextBox> searchBox,
        Action<bool> setFocused) =>
        (FlexRow(
                // Uncontrolled (see Widgets.SearchBox): it starts from the term when it mounts and reports every change. Escape is the
                // tab's command (it empties the box, then closes the search), not the box's own.
                Widgets.SearchBox(term, setTerm, "Search the log", searchBox, setFocused, onKeyDown: e =>
                    {
                        if (e.Key != VirtualKey.Enter)
                            return;
                        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
                        (shift ? previous : next)();
                        e.Handled = true;
                    })
                    .AutomationId("log-search")
                    .AutomationName("Search the log")
                    .ToolTip("Search the log (Ctrl+F). Enter: next match. Shift+Enter: previous.")
                    .Width(320),
                Body(counter).Foreground(Theme.SecondaryText).AutomationId("log-match-count").Width(120),
                Widgets.IconButton(Glyphs.ChevronUp, "Previous match (Shift+Enter)", previous).AutomationId("log-previous").IsEnabled(hasMatches),
                Widgets.IconButton(Glyphs.ChevronDown, "Next match (Enter)", next).AutomationId("log-next").IsEnabled(hasMatches),
                Widgets.IconButton(Glyphs.Close, "Close the search", close).AutomationId("log-search-close"))
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 4,
            });

    /// <summary>
    /// The engine's message, as it gave it, when the log could not be read; Retry opens the page's session again. Not while the page
    /// waits for the engine, when Retry could not work.
    /// </summary>
    private static Element? FailureBar(DetailState detail, bool waiting) =>
        !LogView.ShowsError(detail.LogStatus, waiting)
            ? null
            : Widgets.RetryBar(detail.LogError ?? "", () => AppServices.Current.OpenDetail(detail.ContainerId)).AutomationId("log-error");

    /// <summary>The narrow toolbar at the right edge: Search, Timestamps, Wrap and Colours (kept in the settings), Copy all and Clear.</summary>
    private static Element Toolbar(LogViewOptions options, LogBuffer logs, Action<bool> setSearchShown, bool searchOpen, Action clear)
    {
        var empty = logs.Lines.Count == 0;
        return Border(VStack(
                    4,
                    ToolbarToggle(Glyphs.Search, "Search (Ctrl+F)", searchOpen, setSearchShown).AutomationId("log-search-toggle"),
                    ToolbarToggle(Glyphs.Clock, "Show timestamps", options.Timestamps, SetTimestamps).AutomationId("log-timestamps"),
                    ToolbarToggle(Glyphs.Wrap, "Wrap long lines", options.Wrap, SetWrap).AutomationId("log-wrap"),
                    ToolbarToggle(Glyphs.Palette, "Colours", options.Colors, SetColors).AutomationId("log-colors"),
                    ToolbarButton(Glyphs.Copy, "Copy all", () => ContainerParts.CopyToClipboard(LogSearch.CopyText(logs.Lines, options.Timestamps)))
                        .AutomationId("log-copy")
                        .IsEnabled(!empty),
                    ToolbarButton(Glyphs.Erase, "Clear the view", clear)
                        .AutomationId("log-clear")
                        .IsEnabled(!empty)))
            .AutomationId("log-toolbar")
            .Padding(4)
            .Background(Theme.LayerFill)
            .CornerRadius(8)
            .VAlign(VerticalAlignment.Top)
            .Margin(8, 0, 0, 0);
    }

    private static Element ToolbarToggle(string glyph, string label, bool on, Action<bool> onChanged) =>
        ToggleButton(Widgets.Glyph(glyph), on, onChanged)
            .AutomationName(label)
            .ToolTip(label)
            .Size(ToolbarButtonSize, ToolbarButtonSize)
            .Padding(0);

    private static ButtonElement ToolbarButton(string glyph, string label, Action onClick) =>
        Widgets.IconButton(glyph, label, onClick).Size(ToolbarButtonSize, ToolbarButtonSize).Padding(0);

    private static void SetTimestamps(bool value) =>
        AppServices.Current.UpdateSettings(settings => settings with { Logs = settings.Logs with { Timestamps = value } });

    private static void SetWrap(bool value) =>
        AppServices.Current.UpdateSettings(settings => settings with { Logs = settings.Logs with { Wrap = value } });

    private static void SetColors(bool value) =>
        AppServices.Current.UpdateSettings(settings => settings with { Logs = settings.Logs with { Colors = value } });


    /// <summary>
    /// A line and its position in the buffer, which keys its row. Not its line number: once the buffer is full every batch drops as
    /// many lines as it adds, and with line-number keys Reactor's list diff rebuilt all 10,000 rows on each batch of a busy log (the
    /// UI thread was in that rebuild in 8 of 8 samples). With positions a full buffer changes no keys; the rows in view show their new
    /// lines. A row holds nothing but its line, so a row showing another line is correct.
    /// </summary>
    private readonly record struct LogRow(int Position, LogLine Line);

    /// <summary>The buffer's lines as rows, made as the list asks for them.</summary>
    private sealed class LogRows(LogBuffer buffer) : IReadOnlyList<LogRow>
    {
        public int Count => buffer.Lines.Count;

        public LogRow this[int index] => new(index, buffer.Lines[index]);

        public IEnumerator<LogRow> GetEnumerator()
        {
            var position = 0;
            foreach (var line in buffer.Lines)
                yield return new LogRow(position++, line);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

}

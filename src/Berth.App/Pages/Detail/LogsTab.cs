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

    // The search, the failure, the notes, the log.
    private static readonly GridSize[] MainRows = [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star()];

    // Past this many cached colour brushes (a log drawing 24-bit gradients) the cache starts over.
    private const int MaxColorBrushes = 1024;

    private static readonly Dictionary<(string Key, bool Dark), Brush?> ThemeBrushes = [];

    /// <summary>One brush per colour a styled line uses, shared by every line.</summary>
    private static readonly Dictionary<Windows.UI.Color, SolidColorBrush> ColorBrushes = [];

    /// <summary>The look each row not yet in the window still has to apply when it loads; at most one per row, the latest render's.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, RoutedEventHandler> PendingMarks = new();

    /// <summary>The styled line each row shows in coloured runs, so a render that shows it again changes nothing.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, Painted> PaintedRows = new();

    private static FontFamily? _mono;

    /// <summary>Cascadia Mono, or Consolas where it is not installed; one instance for every line.</summary>
    private static FontFamily Mono => _mono ??= new FontFamily("Cascadia Mono, Consolas");

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
                Grid(MainColumns, OneRow, log, latest).Grid(row: 3))
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
        var look = new LineLook(line, options.Timestamps, colors && !line.Spans.IsEmpty);

        return (TextBlock(LogView.LineText(line, options.Timestamps)) with { LineHeight = LineHeight })
            .FontFamily(Mono)
            .FontSize(LineFontSize)
            .TextWrapping(options.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap)
            .IsTextSelectionEnabled()
            .Foreground(line.Stream == LogStream.Stderr ? Theme.SystemCaution : Theme.PrimaryText)
            .Padding(12, 0, 12, 0)
            .Set(block => Mark(block, look, matches, first, count, offset, currentOnLine, dark));
    }

    /// <summary>
    /// Draws <paramref name="look"/> on <paramref name="block"/> and marks matches <paramref name="first"/> to <paramref name="first"/> +
    /// <paramref name="count"/>, in the colours of the block's own theme; a block not in the window yet is drawn in
    /// <paramref name="appDark"/>'s and again once it is.
    /// </summary>
    private static void Mark(TextBlock block, LineLook look, IReadOnlyList<LogMatch> matches, int first, int count, int offset, int current, bool appDark)
    {
        // Only this render's line and matches may mark the block: drop a mark still waiting from an earlier render.
        if (PendingMarks.TryGetValue(block, out var pending))
        {
            block.Loaded -= pending;
            PendingMarks.Remove(block);
        }

        if (!block.IsLoaded && (count > 0 || look.Styled))
        {
            void OnLoaded(object sender, RoutedEventArgs e) => Mark(block, look, matches, first, count, offset, current, appDark);

            block.Loaded += OnLoaded;
            PendingMarks.Add(block, OnLoaded);
        }

        var dark = block.IsLoaded ? block.ActualTheme == ElementTheme.Dark : appDark;
        var backgrounds = Paint(block, look, dark);

        block.TextHighlighters.Clear();
        foreach (var background in backgrounds)
            block.TextHighlighters.Add(background);
        if (count == 0)
            return;

        var others = new TextHighlighter
        {
            Background = ThemeBrush("SystemFillColorCautionBackgroundBrush", dark),
            Foreground = ThemeBrush("TextFillColorPrimaryBrush", dark),
        };
        var chosen = new TextHighlighter
        {
            Background = ThemeBrush("AccentFillColorDefaultBrush", dark),
            Foreground = ThemeBrush("TextOnAccentFillColorPrimaryBrush", dark),
        };

        for (var i = first; i < first + count; i++)
        {
            var range = new TextRange { StartIndex = offset + matches[i].Start, Length = matches[i].Length };
            (i == current ? chosen : others).Ranges.Add(range);
        }

        if (others.Ranges.Count > 0)
            block.TextHighlighters.Add(others);
        if (chosen.Ranges.Count > 0)
            block.TextHighlighters.Add(chosen);
    }

    /// <summary>
    /// Shows a styled line (<see cref="LineLook.Styled"/>) in runs: plain text as the block's own, each span in its colour, bold, dim
    /// (its colour at 60 %), italic or underlined, its background drawn by a highlighter under the search's marks. Returns those
    /// highlighters. Done once per line, timestamps and theme: a render that shows the same again reuses the runs and highlighters.
    /// A row that showed a styled line and now shows a plain one gets one plain run back (Reactor sets the text only when it changes).
    /// </summary>
    private static TextHighlighter[] Paint(TextBlock block, LineLook look, bool dark)
    {
        PaintedRows.TryGetValue(block, out var painted);
        if (!look.Styled)
        {
            if (painted is not null)
            {
                PaintedRows.Remove(block);
                block.Inlines.Clear();
                block.Inlines.Add(new Run { Text = LogView.LineText(look.Line, look.Timestamps) });
            }

            return [];
        }

        if (painted is not null && ReferenceEquals(painted.Line, look.Line) && painted.Timestamps == look.Timestamps && painted.Dark == dark)
            return painted.Backgrounds;

        var line = look.Line;
        var text = line.Text;
        var offset = LogView.TextOffset(line, look.Timestamps);
        var plainFore = ThemeColor(line.Stream == LogStream.Stderr ? "SystemFillColorCaution" : "TextFillColorPrimary", dark);
        Dictionary<(Windows.UI.Color Back, Windows.UI.Color Fore), TextHighlighter>? backgrounds = null;

        var inlines = block.Inlines;
        inlines.Clear();
        if (offset > 0)
            inlines.Add(new Run { Text = LogView.LineText(line, look.Timestamps)[..offset] });

        var at = 0;
        foreach (var span in line.Spans)
        {
            if (span.Start > at)
                inlines.Add(new Run { Text = text[at..span.Start] });

            var style = span.Style;
            var run = new Run { Text = text.Substring(span.Start, span.Length) };
            Windows.UI.Color? fore = LogColors.Rgb(style.Foreground, dark) is { } rgb ? Opaque(rgb) : null;
            if (style.Effects.HasFlag(TextEffects.Dim))
            {
                var color = fore ?? plainFore;
                fore = Windows.UI.Color.FromArgb((byte)(color.A * 0.6), color.R, color.G, color.B);
            }

            if (fore is { } f)
                run.Foreground = ColorBrush(f);
            if (style.Effects.HasFlag(TextEffects.Bold))
                run.FontWeight = FontWeights.Bold;
            if (style.Effects.HasFlag(TextEffects.Italic))
                run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (style.Effects.HasFlag(TextEffects.Underline))
                run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
            inlines.Add(run);

            if (LogColors.Rgb(style.Background, dark) is { } backRgb)
            {
                backgrounds ??= [];
                var key = (Back: Opaque(backRgb), Fore: fore ?? plainFore);
                if (!backgrounds.TryGetValue(key, out var highlighter))
                {
                    highlighter = new TextHighlighter { Background = ColorBrush(key.Back), Foreground = ColorBrush(key.Fore) };
                    backgrounds[key] = highlighter;
                }

                highlighter.Ranges.Add(new TextRange { StartIndex = offset + span.Start, Length = span.Length });
            }

            at = span.Start + span.Length;
        }

        if (at < text.Length)
            inlines.Add(new Run { Text = text[at..] });

        TextHighlighter[] result = backgrounds is null ? [] : [.. backgrounds.Values];
        PaintedRows.AddOrUpdate(block, new Painted(line, look.Timestamps, dark, result));
        return result;
    }

    private static Windows.UI.Color Opaque(int rgb) => Windows.UI.Color.FromArgb(0xff, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static SolidColorBrush ColorBrush(Windows.UI.Color color)
    {
        if (ColorBrushes.TryGetValue(color, out var brush))
            return brush;
        if (ColorBrushes.Count >= MaxColorBrushes)
            ColorBrushes.Clear();
        brush = new SolidColorBrush(color);
        ColorBrushes[color] = brush;
        return brush;
    }

    /// <summary>The theme colour <paramref name="key"/> of the light or dark theme, from <see cref="ThemeBrush"/>.</summary>
    private static Windows.UI.Color ThemeColor(string key, bool dark) =>
        ThemeBrush(key + "Brush", dark) is SolidColorBrush brush ? brush.Color : Windows.UI.Color.FromArgb(0xff, 0x80, 0x80, 0x80);

    /// <summary>
    /// The theme brush <paramref name="key"/> ("…Brush") of the light or dark theme. Built from the theme's colour of the same name
    /// without "Brush": a theme brush read in code takes its colour from the app's theme, not from the theme it was asked for.
    /// </summary>
    private static Brush? ThemeBrush(string key, bool dark)
    {
        if (ThemeBrushes.TryGetValue((key, dark), out var cached))
            return cached;

        var colorKey = key.EndsWith("Brush", StringComparison.Ordinal) ? key[..^"Brush".Length] : key;
        var theme = dark ? "Default" : "Light";
        Brush? brush = null;
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries.Prepend(Application.Current.Resources))
        {
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed)
                && themed is ResourceDictionary resources
                && resources.TryGetValue(colorKey, out var value)
                && value is Windows.UI.Color color)
            {
                brush = new SolidColorBrush(color);
                break;
            }
        }

        // Not found (a key with no colour of its name): the highlighter's own default colours apply.
        ThemeBrushes[(key, dark)] = brush;
        return brush;
    }

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

    /// <summary>A row's line and how it is drawn: with its timestamp or not, and in its colours (<see cref="Styled"/>) or plain.</summary>
    private sealed record LineLook(LogLine Line, bool Timestamps, bool Styled);

    /// <summary>What <see cref="Paint"/> drew on a row: the line, in which theme, and the highlighters of its backgrounds.</summary>
    private sealed record Painted(LogLine Line, bool Timestamps, bool Dark, TextHighlighter[] Backgrounds);

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

    /// <summary>
    /// The list's scroll viewer: whether the view follows the newest line, kept from its own scrolling (see <see cref="LogView.Follows"/>),
    /// scrolling to the bottom while it does, and bringing a line into view. One per tab, kept across renders.
    /// </summary>
    private sealed class LogScroller
    {
        private ScrollViewer? _view;
        private double _offset;

        public bool Following { get; private set; } = true;

        public Action<bool>? OnFollowingChanged { get; set; }

        public ItemsRepeater? Repeater { get; set; }

        /// <summary>Wrapped lines fit the width; unwrapped ones scroll sideways. Content is not anchored: following decides where the view goes.</summary>
        public static void Configure(ScrollViewer view, bool wrap)
        {
            view.HorizontalScrollMode = wrap ? ScrollMode.Disabled : ScrollMode.Enabled;
            view.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            view.VerticalAnchorRatio = double.NaN;
        }

        public void Attach(ScrollViewer view)
        {
            if (ReferenceEquals(view, _view))
                return;
            _view?.ViewChanged -= OnViewChanged;
            _view = view;
            _offset = view.VerticalOffset;
            view.ViewChanged += OnViewChanged;
        }

        /// <summary>Follows again, from the newest line.</summary>
        public void Follow()
        {
            SetFollowing(true);
            KeepAtBottom();
        }

        public void KeepAtBottom()
        {
            if (!Following || _view is not { } view)
                return;
            view.UpdateLayout();
            view.ChangeView(null, view.ScrollableHeight, null, disableAnimation: true);
        }

        /// <summary>Scrolls the 0-based <paramref name="line"/> into the middle of the view.</summary>
        public void BringIntoView(int line)
        {
            if (Repeater?.GetOrCreateElement(line) is not { } element)
                return;
            element.UpdateLayout();
            element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5, AnimationDesired = false });
        }

        private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_view is not { } view)
                return;
            var follows = LogView.Follows(Following, _offset, view.VerticalOffset, view.ScrollableHeight);
            _offset = view.VerticalOffset;
            SetFollowing(follows);
        }

        private void SetFollowing(bool value)
        {
            if (value == Following)
                return;
            Following = value;
            OnFollowingChanged?.Invoke(value);
        }
    }
}

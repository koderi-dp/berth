using System.Collections;
using System.Globalization;
using Berth.Components;
using Berth.Engine.Models;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Berth.Settings;
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
using TextHighlighter = Microsoft.UI.Xaml.Documents.TextHighlighter;
using TextRange = Microsoft.UI.Xaml.Documents.TextRange;

namespace Berth.Pages.Detail;

/// <summary>What the Logs tab is given.</summary>
/// <param name="Detail">The container's detail state: its log, and where the log stream stands.</param>
/// <param name="ClearLogs">Empties the view (the container's log is kept).</param>
/// <param name="ClaimEscape">
/// Told whether Escape is the search's (it clears the search box) rather than the page's (back to the list); false once the tab goes.
/// </param>
internal sealed record LogsTabProps(DetailState Detail, Action ClearLogs, Action<bool> ClaimEscape);

/// <summary>
/// The "Logs" tab of a container's detail page: the container's log, followed live, in a virtualized list that fills the tab, with a
/// narrow toolbar at its right edge (search, timestamps, wrap, copy all, clear) and a button back to the latest line while scrolled
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

    private static readonly Dictionary<(string Key, bool Dark), Brush?> ThemeBrushes = [];

    /// <summary>The mark each row not yet in the window still has to apply when it loads; at most one per row, the latest render's.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, RoutedEventHandler> PendingMarks = new();

    private static FontFamily? _mono;

    /// <summary>Cascadia Mono, or Consolas where it is not installed; one instance for every line.</summary>
    private static FontFamily Mono => _mono ??= new FontFamily("Cascadia Mono, Consolas");

    public override Element Render()
    {
        var detail = Props.Detail;
        var logs = detail.Logs;
        var options = UseExternalStore(SettingsHook.Subscribe, SettingsHook.Snapshot).Logs;
        var dark = UseIsDarkTheme();
        var (searchOpen, setSearchOpen) = UseState(false);
        var (term, setTerm) = UseState("");
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

        // A new term shows its newest match.
        UseEffect(
            () =>
            {
                if (matches.Count > 0 && anchor is null)
                    scroller.BringIntoView(matches[^1].Line);
            },
            term);

        // Escape clears the search while its box has the focus and text; otherwise it is the page's (back to the list). Only one of
        // the two Escape commands is enabled at a time, so which host WinUI asks first does not matter.
        var escapeClearsSearch = LogView.EscapeClearsSearch(searchFocused, term);
        var claimEscape = Props.ClaimEscape;
        UseEffect(() => claimEscape(escapeClearsSearch), escapeClearsSearch);
        UseEffect(() => () => claimEscape(false), Array.Empty<object>());

        void OpenSearch()
        {
            setSearchOpen(true);
            updateFocusRequests(n => n + 1);
        }

        void CloseSearch()
        {
            setSearchOpen(false);
            setTerm("");
            setAnchor(null);
        }

        void ChangeTerm(string value)
        {
            setTerm(value);
            setAnchor(null);
        }

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

        Command[] shortcuts =
        [
            new()
            {
                Label = "Search the log",
                Execute = OpenSearch,
                Accelerator = Accelerator(VirtualKey.F, VirtualKeyModifiers.Control),
            },
            new()
            {
                Label = "Clear the search",
                Execute = () => ChangeTerm(""),
                CanExecute = escapeClearsSearch,
                Accelerator = Accelerator(VirtualKey.Escape),
            },
        ];

        var emptyText = LogView.EmptyText(detail.LogStatus, logs.Lines.Count, linesSeen: logs.FirstNumber > 1, waitedGeneration == generation);
        var notes = LogView.Notes(detail.LogStatus, logs.Lines.Count, logs.Dropped);
        var log = emptyText is null
            ? LogList(rows, options, matches, current, dark, scroller)
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
                FailureBar(detail)?.Margin(0, 0, 0, 8).Grid(row: 1),
                notes.Count == 0
                    ? null
                    : Caption(string.Join("  ", notes)).Foreground(Theme.SecondaryText).AutomationId("log-notes").Margin(0, 0, 0, 8).Grid(row: 2),
                Grid(MainColumns, OneRow, log, latest).Grid(row: 3))
            .Grid(column: 0);

        var toolbar = Toolbar(options, logs, searchOpen ? CloseSearch : OpenSearch, searchOpen, Clear).Grid(column: 1);

        return CommandHost(shortcuts, Grid(Columns, OneRow, main, toolbar).Margin(0, 0, 0, 16)).Flex(grow: 1, basis: 0);
    }

    /// <summary>The log, one line per row, virtualized: only the rows in view are built, each from its line alone.</summary>
    private static Element LogList(LogRows rows, LogViewOptions options, IReadOnlyList<LogMatch> matches, int current, bool dark, LogScroller scroller) =>
        LazyVStack(rows, row => row.Position.ToString(CultureInfo.InvariantCulture), (row, index) => Line(row.Line, index, options, matches, current, dark))
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
    /// </summary>
    private static Element Line(LogLine line, int index, LogViewOptions options, IReadOnlyList<LogMatch> matches, int current, bool dark)
    {
        var (first, count) = LogView.MatchesOnLine(matches, index);
        var offset = count == 0 ? 0 : LogView.TextOffset(line, options.Timestamps);
        var currentOnLine = current >= first && current < first + count ? current : -1;

        return (TextBlock(LogView.LineText(line, options.Timestamps)) with { LineHeight = LineHeight })
            .FontFamily(Mono)
            .FontSize(LineFontSize)
            .TextWrapping(options.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap)
            .IsTextSelectionEnabled()
            .Foreground(line.Stream == LogStream.Stderr ? Theme.SystemCaution : Theme.PrimaryText)
            .Padding(12, 0, 12, 0)
            .Set(block => Mark(block, matches, first, count, offset, currentOnLine, dark));
    }

    /// <summary>
    /// Marks matches <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> on <paramref name="block"/>, in the
    /// colours of the block's own theme; a block not in the window yet is marked in <paramref name="appDark"/>'s and again once it is.
    /// </summary>
    private static void Mark(TextBlock block, IReadOnlyList<LogMatch> matches, int first, int count, int offset, int current, bool appDark)
    {
        // Only this render's line and matches may mark the block: drop a mark still waiting from an earlier render.
        if (PendingMarks.TryGetValue(block, out var pending))
        {
            block.Loaded -= pending;
            PendingMarks.Remove(block);
        }

        block.TextHighlighters.Clear();
        if (count == 0)
            return;

        if (!block.IsLoaded)
        {
            void OnLoaded(object sender, RoutedEventArgs e) => Mark(block, matches, first, count, offset, current, appDark);

            block.Loaded += OnLoaded;
            PendingMarks.Add(block, OnLoaded);
        }

        var dark = block.IsLoaded ? block.ActualTheme == ElementTheme.Dark : appDark;
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
                TextBox(term, setTerm, placeholderText: "Search the log")
                    .Ref(searchBox)
                    .AutomationId("log-search")
                    .AutomationName("Search the log")
                    .ToolTip("Search the log (Ctrl+F). Enter: next match. Shift+Enter: previous.")
                    .OnGotFocus((_, _) => setFocused(true))
                    .OnLostFocus((_, _) => setFocused(false))
                    .OnKeyDown((_, e) =>
                    {
                        if (e.Key != VirtualKey.Enter)
                            return;
                        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
                        (shift ? previous : next)();
                        e.Handled = true;
                    })
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

    /// <summary>The engine's message, as it gave it, when the log could not be read; Retry opens the page's session again.</summary>
    private static Element? FailureBar(DetailState detail) =>
        detail.LogStatus != StreamStatus.Failed
            ? null
            : (InfoBar(message: detail.LogError ?? "") with
            {
                IsOpen = true,
                ActionButtonContent = "Retry",
                OnActionButtonClick = () => AppServices.Current.OpenDetail(detail.ContainerId),
            })
                .Error()
                .IsClosable(false)
                .AutomationId("log-error");

    /// <summary>The narrow toolbar at the right edge: Search, Timestamps and Wrap (kept in the settings), Copy all and Clear.</summary>
    private static Element Toolbar(LogViewOptions options, LogBuffer logs, Action toggleSearch, bool searchOpen, Action clear)
    {
        var empty = logs.Lines.Count == 0;
        return Border(VStack(
                    4,
                    ToolbarToggle(Glyphs.Search, "Search (Ctrl+F)", searchOpen, _ => toggleSearch()).AutomationId("log-search-toggle"),
                    ToolbarToggle(Glyphs.Clock, "Show timestamps", options.Timestamps, SetTimestamps).AutomationId("log-timestamps"),
                    ToolbarToggle(Glyphs.Wrap, "Wrap long lines", options.Wrap, SetWrap).AutomationId("log-wrap"),
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

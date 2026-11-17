using System.Collections.Immutable;
using Berth.Components;
using Berth.Hooks;
using Berth.Pages.Detail;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>
/// The Logs page: the logs of every container on the connection together, merged by time, in a virtualized table (checkbox, chevron,
/// timestamp, container, message) with search (plain or <c>/regex/</c>, case toggle), a filter by container or compose project, stream and
/// running only, copy, save to a file, and "Stick to bottom". What it shows is decided in <see cref="LogsView"/> and
/// <see cref="LogsQuery"/>; the lines come from the logs session (<see cref="AppServices.Logs"/>), which <c>App</c> opens and closes.
/// This holds only view state.
/// </summary>
internal sealed class LogsPage : EngineStateComponent
{
    private const string SearchPlaceholder = "Search logs (supports regex like \"/error|warn/\")";

    private static readonly GridSize[] ToolbarColumns = [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto];
    private static readonly GridSize[] OneRow = [GridSize.Star()];
    private static readonly GridSize[] TableRows = [GridSize.Auto, GridSize.Star(), GridSize.Auto];
    private static readonly GridSize[] FooterColumns = [GridSize.Star(), GridSize.Auto];
    private static readonly GridSize[] OneColumn = [GridSize.Star()];
    private static readonly FileTypeChoice[] LogFileTypes = [new FileTypeChoice("Log file", (string[])[".log"])];
    private static readonly object NoSources = new();

    public override Element Render()
    {
        var state = UseEngineState();
        var logs = UseExternalStore(LogsStateHook.Subscribe, LogsStateHook.Snapshot, LogsStateHook.SameState) ?? LogsState.Initial;
        var colors = UseExternalStore(SettingsHook.Subscribe, SettingsHook.Snapshot).Logs.Colors;
        var dark = UseIsDarkTheme();
        var highContrast = UseHighContrast();
        var nav = UseNavigation<AppRoute>();

        var (search, setSearch) = UseState("");
        var (matchCase, setMatchCase) = UseState(false);
        var (filter, updateFilter) = UseReducer(LogsFilter.All);
        var (showTime, setShowTime) = UseState(true);
        var (showObject, setShowObject) = UseState(true);
        var (checkedLines, updateChecked) = UseReducer(ImmutableHashSet<long>.Empty);
        var (expanded, updateExpanded) = UseReducer(ImmutableHashSet<long>.Empty);
        var (following, setFollowing) = UseState(true);
        var (saveError, setSaveError) = UseState<string?>(null);
        var searchBox = this.UseElementRef<TextBox>();
        var scroller = UseMemo(() => new LogScroller(), Array.Empty<object>());
        scroller.OnFollowingChanged = setFollowing;
        var handlers = UseMemo(() => new LogsRowHandlers(), Array.Empty<object>());

        // A switch of connection: the lines checked, expanded or filtered by belonged to the old engine.
        UseEffect(
            () =>
            {
                updateChecked(_ => ImmutableHashSet<long>.Empty);
                updateExpanded(_ => ImmutableHashSet<long>.Empty);
                updateFilter(f => f with { Objects = null });
                setSaveError(null);
            },
            AppServices.Current.SessionNumber);

        var query = UseMemo(() => LogsQuery.Parse(search, matchCase), search, matchCase);
        // Running only reads each container's state; otherwise the lines alone decide. The filter and the search are one dependency: a
        // token made again only when either changes.
        var criteria = UseMemo(() => new object(), filter, query);
        var shown = UseMemo(
            () => LogsView.Shown(logs, filter, query),
            logs.Log,
            filter.RunningOnly ? logs.Sources : NoSources,
            criteria);
        var rows = UseMemo(() => new LogsRows(shown.Entries), shown);
        var objects = UseMemo(() => LogsView.Objects(logs), logs.Sources);

        // While following, the newest line stays in view as lines arrive and when rows change height.
        UseEffect(() => scroller.KeepAtBottom(), shown, expanded, showTime, showObject);

        handlers.SetChecked = (sequence, on) => updateChecked(set => on ? set.Add(sequence) : set.Remove(sequence));
        handlers.ToggleExpanded = sequence => updateExpanded(set => set.Contains(sequence) ? set.Remove(sequence) : set.Add(sequence));
        handlers.OpenContainer = id => nav.Navigate(
            new AppRoute.ContainerDetail(id, AppServices.Current.SessionNumber),
            new NavigateOptions { Transition = NavigationTransition.DrillIn() });

        // Ctrl+F: the search box, its text selected.
        var keys = UseMemo(
            () => new PageKeys(Search: () =>
            {
                if (searchBox.Current is { } box)
                {
                    box.Focus(FocusState.Keyboard);
                    box.SelectAll();
                }
            }),
            Array.Empty<object>());
        UseEffect(() => ShellKeys.Register(AppRoute.Logs, keys), keys);

        var entries = shown.Entries;
        var selectedShown = checkedLines.IsEmpty ? 0 : entries.Count(e => checkedLines.Contains(e.Sequence));
        IEnumerable<LogEntry> ToCopy() => selectedShown > 0 ? entries.Where(e => checkedLines.Contains(e.Sequence)) : entries;

        void Copy() => ContainerParts.CopyToClipboard(LogsView.CopyText(ToCopy(), showTime, showObject));

        async void Save()
        {
            var text = LogsView.CopyText(entries, showTime, showObject);
            var picked = await PickSaveFileAsync(new SaveFilePickerOptions(LogFileTypes, LogsView.SaveName(DateTimeOffset.Now), CommitButtonText: "Save")
            {
                Title = "Save the shown lines",
                DefaultFileExtension = ".log",
            });
            if (picked?.Path is not { Length: > 0 } path)
                return;
            try
            {
                await File.WriteAllTextAsync(path, text);
                setSaveError(null);
            }
            catch (Exception ex)
            {
                AppLog.Error("saving the shown logs failed", ex);
                setSaveError("The file could not be saved: " + ex.Message);
            }
        }

        void CheckAll(bool on) =>
            updateChecked(set => on ? set.Union(entries.Select(e => e.Sequence)) : set.Except(entries.Select(e => e.Sequence)));

        var waiting = logs.ConnectionLost || (AppServices.Current.Logs is null && state.Status != ConnectionStatus.Connected);
        var notes = LogsView.Notes(logs, waiting && logs.Log.Entries.Count > 0);
        var narrowed = filter.IsActive || !query.IsEmpty;

        var toolbar = Toolbar(
            search,
            setSearch,
            searchBox,
            matchCase,
            setMatchCase,
            shown.QueryError,
            FilterButton(filter, updateFilter, objects),
            ColumnsButton(showTime, showObject, colors, setShowTime, setShowObject),
            Widgets.IconButton(Glyphs.Copy, selectedShown > 0 ? "Copy the selected lines" : "Copy the shown lines", Copy)
                .AutomationId("logs-copy")
                .IsEnabled(entries.Count > 0),
            Widgets.IconButton(Glyphs.Download, "Save the shown lines as a .log file…", Save)
                .AutomationId("logs-save")
                .IsEnabled(entries.Count > 0));

        var summary = notes.Count == 0 && saveError is null
            ? null
            : VStack(
                4,
                notes.Count == 0 ? Empty() : Caption(string.Join("  ", notes)).Foreground(Theme.SecondaryText).AutomationId("logs-notes"),
                saveError is null ? Empty() : Caption(saveError).Foreground(Theme.SystemCritical).AutomationId("logs-save-error"))
                .Flex(shrink: 0);

        var emptyText = LogsView.EmptyText(logs, entries.Count, waiting);
        var body = emptyText is null
            ? LogsTable.List(
                rows,
                new LogsRowView(showTime, showObject, colors && !highContrast, dark, query, checkedLines, expanded, logs.Sources, handlers),
                scroller)
            : Widgets.CenteredNote(emptyText).AutomationId("logs-empty");

        var latest = following || emptyText is not null
            ? null
            : Button(Widgets.Glyph(Glyphs.ArrowDown), scroller.Follow)
                .AutomationId("logs-latest")
                .AutomationName("Jump to the latest line")
                .ToolTip("Jump to the latest line")
                .Size(36, 36)
                .Padding(0)
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Bottom)
                .Margin(0, 0, 12, 12);

        var footer = Grid(
                FooterColumns,
                OneRow,
                Body(LogsView.CountText(entries.Count, shown.Total, narrowed))
                    .Foreground(Theme.SecondaryText)
                    .AutomationId("logs-count")
                    .VAlign(VerticalAlignment.Center)
                    .Grid(column: 0),
                LogsTab.Footer(following, scroller).AutomationId("logs-stick").Grid(column: 1))
            .Margin(0, 4, 0, 12);

        // A grid, not a flex column: layout runs on every batch of lines, and a flex column also measures the list for its minimum size.
        var table = Grid(
                OneColumn,
                TableRows,
                LogsTable.Header(showTime, showObject, selectedShown == 0 ? false : selectedShown == entries.Count ? true : null, entries.Count > 0, CheckAll)
                    .Grid(row: 0),
                Grid(OneColumn, OneRow, body, latest).Grid(row: 1),
                footer.Grid(row: 2))
            .Flex(grow: 1, basis: 0);

        var bulk = selectedShown == 0
            ? null
            : ListPage.BulkBar(
                selectedShown,
                Button("Copy", Copy).AutomationId("logs-copy-selected"),
                Button("Clear selection", () => updateChecked(_ => ImmutableHashSet<long>.Empty)).AutomationId("logs-clear-selection"));

        return ListPage.Render("Logs", state, summary, toolbar, bulk, table);
    }

    /// <summary>
    /// The row under the title: the search box with the case toggle ("Aa") beside it and why a pattern is not valid under it, and at the
    /// right the filter, columns, copy and save buttons.
    /// </summary>
    private static Element Toolbar(
        string search,
        Action<string> setSearch,
        ElementRef<TextBox> searchBox,
        bool matchCase,
        Action<bool> setMatchCase,
        string? queryError,
        params Element[] buttons)
    {
        var box = Widgets.SearchBox(search, setSearch, SearchPlaceholder, searchBox, _ => { }, clearOnEscape: true)
            .AutomationId("logs-search")
            .AutomationName("Search logs")
            .ToolTip("Search logs (Ctrl+F). Write /…/ for a regular expression, such as /error|warn/.")
            .Width(360);
        var caseToggle = ToggleButton(Body("Aa"), matchCase, setMatchCase)
            .AutomationId("logs-match-case")
            .AutomationName("Match case")
            .ToolTip("Match case")
            .MinWidth(40)
            .Margin(8, 0, 0, 0);

        var row = Grid(
                ToolbarColumns,
                OneRow,
                box.VAlign(VerticalAlignment.Center).Grid(column: 0),
                caseToggle.VAlign(VerticalAlignment.Center).Grid(column: 1),
                HStack(4, buttons).VAlign(VerticalAlignment.Center).Grid(column: 3))
            .Flex(shrink: 0);

        return queryError is null
            ? row
            : VStack(4, row, Caption(queryError).Foreground(Theme.SystemCritical).AutomationId("logs-search-error")).Flex(shrink: 0);
    }

    /// <summary>
    /// The filter button and its flyout: a checklist of compose projects and containers with "Select all", the two streams, and running
    /// containers only. Its glyph is in the accent colour while the filter hides anything.
    /// </summary>
    private static Element FilterButton(LogsFilter filter, Action<Func<LogsFilter, LogsFilter>> update, ImmutableArray<LogObject> objects)
    {
        var keys = objects.Select(o => o.Key).ToArray();
        bool? allState = filter.Objects is null ? true : objects.All(o => filter.Objects.Contains(o.Key)) ? true : objects.Any(o => filter.Objects.Contains(o.Key)) ? null : false;

        var checklist = VStack(
            0,
            [
                CheckBox(allState, on => update(f => f with { Objects = on ? null : ImmutableHashSet<string>.Empty }), "Select all")
                    .AutomationId("logs-filter-all"),
                .. objects.Select(o => (Element)CheckBox(
                        filter.Objects?.Contains(o.Key) ?? true,
                        on => update(f => f.WithObject(o.Key, on, keys)),
                        o.Label)
                    .AutomationId("logs-filter-" + o.Label)),
            ]);

        var content = VStack(
                8,
                Caption("Containers and projects").Foreground(Theme.SecondaryText),
                objects.IsEmpty ? Caption("None yet.").Foreground(Theme.SecondaryText) : ScrollViewer(checklist).MaxHeight(280),
                Caption("Streams").Foreground(Theme.SecondaryText).Margin(0, 4, 0, 0),
                CheckBox(filter.Stdout, on => update(f => f with { Stdout = on }), "stdout").AutomationId("logs-filter-stdout"),
                CheckBox(filter.Stderr, on => update(f => f with { Stderr = on }), "stderr").AutomationId("logs-filter-stderr"),
                CheckBox(filter.RunningOnly, on => update(f => f with { RunningOnly = on }), "Running containers only")
                    .AutomationId("logs-filter-running")
                    .Margin(0, 4, 0, 0),
                Button("Show everything", () => update(_ => LogsFilter.All))
                    .AutomationId("logs-filter-reset")
                    .IsEnabled(filter.IsActive)
                    .Margin(0, 4, 0, 0))
            .Width(280);

        return Widgets.IconButton(Glyphs.Filter, filter.IsActive ? "Filter (on)" : "Filter", null, color: filter.IsActive ? Theme.AccentText : null)
            .AutomationId("logs-filter")
            .WithFlyout(ContentFlyout(content));
    }

    // The columns menu, by what it shows: built again only when a choice changes, so the button is not handed a new menu on every render.
    private static readonly Dictionary<(bool Time, bool Object, bool Colors), Element> ColumnMenus = [];
    private static Action<bool> _setShowTime = _ => { };
    private static Action<bool> _setShowObject = _ => { };

    /// <summary>The columns control: Timestamp and Object shown or not, and the Colours option the Logs tab shares.</summary>
    private static Element ColumnsButton(bool showTime, bool showObject, bool colors, Action<bool> setShowTime, Action<bool> setShowObject)
    {
        _setShowTime = setShowTime;
        _setShowObject = setShowObject;
        if (!ColumnMenus.TryGetValue((showTime, showObject, colors), out var menu))
        {
            menu = MenuItems(
            [
                ToggleMenuItem("Timestamp", showTime, on => _setShowTime(on)),
                ToggleMenuItem("Object", showObject, on => _setShowObject(on)),
                MenuSeparator(),
                ToggleMenuItem("Colours", colors, SetColors),
            ]);
            ColumnMenus[(showTime, showObject, colors)] = menu;
        }

        return Widgets.IconButton(Glyphs.Columns, "Columns and colours", null)
            .AutomationId("logs-columns")
            .WithFlyout(menu);
    }

    private static void SetColors(bool value) =>
        AppServices.Current.UpdateSettings(settings => settings with { Logs = settings.Logs with { Colors = value } });
}

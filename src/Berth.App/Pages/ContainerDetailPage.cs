using System.Collections.Immutable;
using System.Globalization;
using Berth.Components;
using Berth.Engine.Models;
using Berth.Hooks;
using Berth.Pages.Detail;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>What the app gives a container's detail page.</summary>
/// <param name="Route">The page's route: the container it shows, and on which engine session.</param>
/// <param name="Leave">Returns to the list; with a text, the list shows it in an info bar ("web-1 was removed.").</param>
internal sealed record DetailPageProps(AppRoute.ContainerDetail Route, Action<string?> Leave);

/// <summary>
/// One container: its header with its state and actions, and the Logs, Inspect, Bind mounts and Stats tabs. While the page is the
/// one shown it keeps a detail session open for its container (<see cref="AppServices.OpenDetail"/>), opens it again on the new
/// connection when the engine reconnects, and has it read the container again when a list load shows it may have run again. A
/// container that is removed sends the page back to the list. What to do when is decided in <see cref="DetailPage"/>.
/// </summary>
internal sealed class ContainerDetailPage : DetailStateComponent<DetailPageProps>
{
    private static readonly ContainerFilter NoFilter = new("", RunningOnly: false);

    // What the detail session effect follows besides the engine state: whether the session is open, removed or its connection lost.
    private static readonly object SessionOpen = new();
    private static readonly object SessionRemoved = new();
    private static readonly object SessionLost = new();

    private static readonly SelectorBarItemData[] Tabs =
        [SelectorBarItem("Logs"), SelectorBarItem("Inspect"), SelectorBarItem("Bind mounts"), SelectorBarItem("Stats")];

    public override Element Render()
    {
        var id = Props.Route.ContainerId;
        var leave = Props.Leave;
        var state = UseEngineState();
        var detail = UseDetailState();
        var compact = !UseBreakpoint(ContainerRowView.WideLayoutMinWidth);
        var nav = UseNavigation<AppRoute>();
        var (tab, setTab) = UseState(0);
        var (deleteRequest, setDeleteRequest) = UseState<DeleteRequest?>(null);
        // What the Logs tab does with Ctrl+F and Escape while it is shown; the page owns the shortcuts.
        var logsKeys = UseMemo(() => new LogsTabKeys(), Array.Empty<object>());
        var (logSearchRequests, updateLogSearchRequests) = UseReducer(0);
        // The Logs tab's search lives here, so it outlives the tab being mounted again (another tab chosen and back, or a ring shown).
        var (logSearch, updateLogSearch) = UseReducer(LogSearchState.Closed);
        var lastName = UseRef<string?>(null);
        var lastList = UseRef(default(ImmutableArray<ContainerSummary>));
        var lastRow = UseRef<ContainerRow?>(null);
        var lastLive = UseRef<DetailState?>(null);

        // Reactor keeps a page it navigated away from, so the list keeps its search and scroll; such a page does nothing.
        var active = nav.CurrentRoute == Props.Route;
        var connected = state.Status == ConnectionStatus.Connected;
        var row = UseMemo(() => FindRow(state, id), state.Containers, state.Pending);
        var mine = detail?.ContainerId == id ? detail : null;
        // Where Back goes, from the page this one was opened from; kept while another page is shown.
        var backTarget = UseRef(BackTarget.Containers);
        if (active)
            backTarget.Current = AppRoute.BackTargetOf(nav.CanGoBack ? nav.BackStack[^1] : null);
        var backLabel = backTarget.Current.Label;

        // While the session is closed or reopened, or its connection is lost, the tabs show the last live state, so they stay mounted
        // and keep what the user was doing (an edit in the log search), and say they wait for the engine rather than that the
        // container stopped (see DetailPage).
        lastLive.Current = DetailPage.KeepLive(mine, lastLive.Current);
        var shown = DetailPage.Shown(mine, lastLive.Current, id);
        var waiting = DetailPage.Waiting(shown, mine, connected);
        if (row is not null)
            lastName.Current = row.Name;
        var name = row?.Name ?? lastName.Current ?? mine?.Details?.Name ?? id;

        // The detail session: DetailPage.Next decides from the latest session state whether to open, close, refresh or leave; this
        // only carries it out. While no session is open every engine state change asks again, so a reconnect the page saw only in
        // part still opens one. A list load counts once: the row it showed is kept for the next.
        object sessionKey = mine is null ? state : mine.Removed ? SessionRemoved : mine.ConnectionLost ? SessionLost : SessionOpen;
        UseEffect(
            () =>
            {
                var open = AppServices.Current.Detail;
                var session = open?.State is { } current && current.ContainerId == id ? current : null;
                var reloaded = state.Containers != lastList.Current;
                var previousRow = lastRow.Current;
                if (reloaded)
                {
                    lastList.Current = state.Containers;
                    lastRow.Current = row;
                }

                switch (DetailPage.Next(new(active, connected, state.ListLoaded, reloaded, previousRow, row, session)))
                {
                    case DetailPageStep.Open:
                        AppServices.Current.OpenDetail(id);
                        break;
                    case DetailPageStep.Close:
                        AppServices.Current.CloseDetail();
                        break;
                    case DetailPageStep.Refresh:
                        open?.Refresh();
                        break;
                    case DetailPageStep.Leave:
                        leave($"{name} was removed.");
                        break;
                }
            },
            active,
            connected,
            state.ListLoaded,
            state.Containers,
            state.Pending,
            sessionKey);

        // The shortcuts, none while the delete confirmation is open. Ctrl+F searches the log (choosing the Logs tab), F5 reads the
        // container and the list again, Delete asks to delete the container, Escape is the log search's while its box has the focus
        // and otherwise goes back to the list, as Alt+Left does.
        var keys = deleteRequest is not null
            ? new PageKeys()
            : new PageKeys(
                Search: () =>
                {
                    logsKeys.FocusSearch = true;
                    updateLogSearch(s => s.Opened());
                    updateLogSearchRequests(n => n + 1);
                    setTab(0);
                },
                Reload: connected ? () => Reload(id) : null,
                Delete: row is not null && RowSelection.CanAct(row, connected) ? () => setDeleteRequest(DeleteRequest.For((ContainerRow[])[row])) : null,
                Escape: () =>
                {
                    if (logsKeys.Escape is { } escape)
                        escape();
                    else
                        leave(null);
                },
                Back: () => leave(null));
        var route = Props.Route;
        UseEffect(() => ShellKeys.Register(route, keys), keys);

        void Run(RowAction action)
        {
            if (action.Kind == RowActionKind.Delete && row is not null)
                setDeleteRequest(DeleteRequest.For((ContainerRow[])[row]));
            else if (action.EngineAction is { } engineAction && action.Ids.Length > 0)
                AppServices.Current.Session.Dispatch(new ActionRequested(action.Ids, engineAction));
        }

        // The page renders on every batch of log lines (ten a second while a log floods). The header and the tab strip are built again
        // only when what they show changes; the header's "Running (52 minutes ago)" at most once a minute.
        var details = shown?.Details;
        // One key for the rest of what the header shows: the minute, whether connected, whether compact, and where Back goes.
        var headerKey = string.Concat(((DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMinute * 4) + (connected ? 2 : 0) + (compact ? 1 : 0)).ToString(CultureInfo.InvariantCulture), "|", backLabel);
        var header = UseMemo(
            () => row is null ? null : DetailHeader.Render(row, details, connected, compact, backLabel, () => leave(null), Run).WithKey("header"),
            row,
            details,
            headerKey);
        var tabStrip = UseMemo(() => SelectorBar(Tabs, tab, setTab).AutomationId("detail-tabs").Flex(shrink: 0).WithKey("tabs"), tab);

        // Keyed, so a bar coming or going (the connection bar during an outage) does not move the tabs to another slot and mount them
        // again.
        var page = (FlexColumn(
                header,
                ConnectionBar.Render(state, () => AppServices.Current.Session.Dispatch(new ConnectRequested()))?.WithKey("connection"),
                LoadErrorBar(mine?.LoadError, connected ? id : null)?.WithKey("load-error"),
                tabStrip,
                Content(shown, waiting, row is not null, tab, new LogsTabHooks(logSearch, updateLogSearch, logsKeys, logSearchRequests), state.Info?.MemoryBytes ?? 0)
                    .Flex(grow: 1, basis: 0)
                    .WithKey("content"),
                DeleteDialog.Render(deleteRequest, () => setDeleteRequest(null))?.WithKey("delete-dialog"))
            with
            {
                RowGap = 12,
            })
            .FlexPadding(24, 16, 24, 0);

        return page.Flex(grow: 1, basis: 0);
    }

    /// <summary>F5: reads the container again and the lists the session shows.</summary>
    private static void Reload(string id)
    {
        Refresh(id);
        AppServices.Current.Session.Dispatch(new ReloadRequested());
    }

    /// <summary>The list's row for <paramref name="id"/>, or null when the list does not have it (not loaded yet, or removed).</summary>
    private static ContainerRow? FindRow(EngineState state, string id) =>
        ComposeGrouping.Build(state.Containers, state.Pending, NoFilter).SelectMany(g => g.Rows).FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// The last failed read of the container, in the engine's words; gone once a read succeeds. Retry reads it again (the streams start
    /// after a first read that succeeds late); only while connected (<paramref name="retryId"/>, the container, is null otherwise).
    /// </summary>
    private static Element? LoadErrorBar(string? error, string? retryId)
    {
        if (error is null)
            return null;

        return Widgets.RetryBar(error, retryId is null ? null : () => Refresh(retryId)).AutomationId("detail-error").Flex(shrink: 0);
    }

    /// <summary>Has the open detail session read the container again, when it is this container's.</summary>
    private static void Refresh(string id)
    {
        if (AppServices.Current.Detail is { } session && session.State.ContainerId == id)
            session.Refresh();
    }

    /// <summary>
    /// The chosen tab, once the container has been read; a ring until then. Each tab is a component whose props are what it shows, so
    /// it renders again only when that changes, not on every batch of log lines. While <paramref name="waiting"/> for the engine the
    /// Logs and Stats tabs say so instead of what their streams say (see <see cref="DetailPage.Waiting"/>).
    /// </summary>
    private static Element Content(DetailState? detail, bool waiting, bool listed, int tab, LogsTabHooks logs, long machineMemory)
    {
        if (detail?.Details is null)
        {
            return listed && detail?.LoadError is null
                ? ProgressRing().Size(32, 32).AutomationName("Loading the container").HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Center).VAlign(Microsoft.UI.Xaml.VerticalAlignment.Top).Margin(0, 24, 0, 0)
                : Empty();
        }

        return tab switch
        {
            1 => Component<InspectTab, InspectTabProps>(new(detail.Details, detail.RawJson ?? "")),
            2 => Component<BindMountsTab, BindMountsTabProps>(new(detail.Details)),
            3 => Component<StatsTab, StatsTabProps>(new(
                detail.ContainerId, detail.Stats, detail.StatsStatus, detail.StatsError, waiting, detail.Details.CpuLimit, machineMemory)),
            _ => Component<LogsTab, LogsTabProps>(new(detail, waiting, ClearLogs, logs.Search, logs.UpdateSearch, logs.Keys, logs.SearchRequests)),
        };
    }

    /// <summary>What the page keeps for the Logs tab: its search, and what the tab does with Ctrl+F and Escape.</summary>
    private sealed record LogsTabHooks(LogSearchState Search, Action<Func<LogSearchState, LogSearchState>> UpdateSearch, LogsTabKeys Keys, int SearchRequests);

    private static void ClearLogs() => AppServices.Current.Detail?.ClearLogs();
}

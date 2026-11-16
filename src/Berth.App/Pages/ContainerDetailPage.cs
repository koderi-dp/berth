using Berth.Components;
using Berth.Hooks;
using Berth.Pages.Detail;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>What the app gives a container's detail page.</summary>
/// <param name="Route">The page's route: the container it shows, and on which engine session.</param>
/// <param name="Leave">Returns to the list; with a text, the list shows it in an info bar ("web-1 was removed.").</param>
internal sealed record DetailPageProps(AppRoute.ContainerDetail Route, Action<string?> Leave);

/// <summary>
/// One container: its header with its state and actions, and the Logs, Inspect, Bind mounts and Stats tabs. While the page is the
/// one shown it keeps a detail session open for its container (<see cref="AppServices.OpenDetail"/>), opens it again on the new
/// connection when the engine reconnects, and has it read the container again when the list shows its state changed. A container
/// that is removed sends the page back to the list.
/// </summary>
internal sealed class ContainerDetailPage : DetailStateComponent<DetailPageProps>
{
    private static readonly ContainerFilter NoFilter = new("", RunningOnly: false);

    // What the detail session effect follows besides the engine state: whether the session is open or its connection was lost.
    private static readonly object SessionOpen = new();
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
        var lastName = UseRef<string?>(null);
        var lastRowState = UseRef<(RowState State, ContainerAction? Pending)?>(null);

        // Reactor keeps a page it navigated away from, so the list keeps its search and scroll; such a page does nothing.
        var active = nav.CurrentRoute == Props.Route;
        var connected = state.Status == ConnectionStatus.Connected;
        var row = UseMemo(() => FindRow(state, id), state.Containers, state.Pending);
        var mine = detail?.ContainerId == id ? detail : null;
        if (row is not null)
            lastName.Current = row.Name;
        var name = row?.Name ?? lastName.Current ?? mine?.Details?.Name ?? id;

        // The detail session: open while the page is shown and connected; closed when its connection is lost and opened again on the
        // next one. While no session is open, every engine state change tries again, so a reconnect the page saw only in part still
        // opens it.
        object sessionKey = mine is null ? state : mine.ConnectionLost ? SessionLost : SessionOpen;
        UseEffect(
            () =>
            {
                if (!active)
                    return;
                if (mine is { ConnectionLost: true })
                    AppServices.Current.CloseDetail();
                else if (mine is null && connected)
                    AppServices.Current.OpenDetail(id);
            },
            active,
            connected,
            sessionKey);

        // Started, stopped, restarted (an action on it ended): read it again, which also follows a new start's log and stats.
        (RowState, ContainerAction?)? rowState = row is null ? null : (row.State, row.Pending);
        UseEffect(
            () =>
            {
                var previous = lastRowState.Current;
                lastRowState.Current = rowState;
                if (active && previous is not null && rowState is { Item2: null } now && previous != now
                    && AppServices.Current.Detail is { } session && session.State.ContainerId == id)
                {
                    session.Refresh();
                }
            },
            rowState);

        // Removed, from this page or elsewhere: back to the list, which says so.
        var gone = mine is { Removed: true } || (connected && state.ListLoaded && row is null);
        UseEffect(
            () =>
            {
                if (active && gone)
                    leave($"{name} was removed.");
            },
            active,
            gone);

        var dialogOpen = deleteRequest is not null;
        Command[] shortcuts =
        [
            new()
            {
                Label = "Back to containers",
                Execute = () => leave(null),
                CanExecute = !dialogOpen,
                Accelerator = Accelerator(VirtualKey.Escape),
            },
            new()
            {
                Label = "Back to containers",
                Execute = () => leave(null),
                CanExecute = !dialogOpen,
                Accelerator = Accelerator(VirtualKey.Left, VirtualKeyModifiers.Menu),
            },
        ];

        void Run(RowAction action)
        {
            if (action.Kind == RowActionKind.Delete && row is not null)
                setDeleteRequest(DeleteRequest.For((ContainerRow[])[row]));
            else if (action.EngineAction is { } engineAction && action.Ids.Length > 0)
                AppServices.Current.Session.Dispatch(new ActionRequested(action.Ids, engineAction));
        }

        var page = (FlexColumn(
                row is null
                    ? null
                    : DetailHeader.Render(row, mine?.Details?.State, connected, compact, () => leave(null), Run),
                ConnectionBar.Render(state, () => AppServices.Current.Session.Dispatch(new ConnectRequested())),
                LoadErrorBar(mine?.LoadError),
                SelectorBar(Tabs, tab, setTab).AutomationId("detail-tabs").Flex(shrink: 0),
                Content(mine, row is not null, tab).Flex(grow: 1, basis: 0),
                DeleteDialog.Render(deleteRequest, () => setDeleteRequest(null)))
            with
            {
                RowGap = 12,
            })
            .FlexPadding(24, 16, 24, 0);

        return CommandHost(shortcuts, page).Flex(grow: 1, basis: 0);
    }

    /// <summary>The list's row for <paramref name="id"/>, or null when the list does not have it (not loaded yet, or removed).</summary>
    private static ContainerRow? FindRow(EngineState state, string id) =>
        ComposeGrouping.Build(state.Containers, state.Pending, NoFilter).SelectMany(g => g.Rows).FirstOrDefault(r => r.Id == id);

    /// <summary>The last failed read of the container, in the engine's words; gone once a read succeeds.</summary>
    private static Element? LoadErrorBar(string? error) =>
        error is null
            ? null
            : (InfoBar(message: error) with { IsOpen = true })
                .Error()
                .IsClosable(false)
                .Flex(shrink: 0);

    /// <summary>The chosen tab, once the container has been read; a ring until then.</summary>
    private static Element Content(DetailState? detail, bool listed, int tab)
    {
        if (detail?.Details is null)
        {
            return listed && detail?.LoadError is null
                ? ProgressRing().Size(32, 32).AutomationName("Loading the container").HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Center).VAlign(Microsoft.UI.Xaml.VerticalAlignment.Top).Margin(0, 24, 0, 0)
                : Empty();
        }

        return tab switch
        {
            1 => InspectTab.Render(detail),
            2 => BindMountsTab.Render(detail),
            3 => StatsTab.Render(detail),
            _ => Component<LogsTab, LogsTabProps>(new(detail, ClearLogs)),
        };
    }

    private static void ClearLogs() => AppServices.Current.Detail?.ClearLogs();
}

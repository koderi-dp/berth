using Berth.Components;
using Berth.Hooks;
using Berth.Pages;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth;

/// <summary>Where the app is: the containers list, the settings, or one container's detail page.</summary>
internal abstract record AppRoute
{
    public static readonly AppRoute Containers = new Page("containers");

    public static readonly AppRoute Settings = new Page("settings");

    /// <summary>The detail page of the container <paramref name="ContainerId"/>; opened from the list, which it returns to.</summary>
    public sealed record ContainerDetail(string ContainerId) : AppRoute;

    private sealed record Page(string Name) : AppRoute;
}

/// <summary>The main window: title bar with the connection switcher, the navigation and its page, the error bar and the status bar.</summary>
internal sealed class App : EngineStateComponent
{
    private const string ContainersTag = "containers";

    private const string SettingsSetAsideText =
        "The settings file could not be read. It was kept as settings.bad.json and Berth started with defaults.";

    public override Element Render()
    {
        var state = UseEngineState();
        var settings = UseExternalStore(SettingsHook.Subscribe, SettingsHook.Snapshot);
        var nav = UseNavigation(AppRoute.Containers);
        var (isPaneOpen, setIsPaneOpen) = UseState(true);
        var (settingsSetAside, setSettingsSetAside) = UseState(AppServices.Current.SettingsWereSetAside);
        var (notice, setNotice) = UseState<string?>(null);

        // Back to the list from a detail page, with an optional note for the list ("web-1 was removed."). The list is usually the page
        // before; going back to it restores its search, sort and scroll.
        void LeaveDetail(string? note)
        {
            setNotice(note);
            if (nav.CurrentRoute is not AppRoute.ContainerDetail)
                return;
            if (nav.CanGoBack && nav.BackStack[^1] == AppRoute.Containers)
                nav.GoBack();
            else
                nav.Replace(AppRoute.Containers);
        }

        // The detail session belongs to the detail page: none stays open on any other page. A note from the last one goes when the next opens.
        UseEffect(
            () =>
            {
                if (nav.CurrentRoute is AppRoute.ContainerDetail)
                    setNotice(null);
                else
                    AppServices.Current.CloseDetail();
            },
            nav.CurrentRoute);

        // A switch of connection closes the detail session (it ran on the old connection) and returns to the list.
        UseEffect(
            () =>
            {
                void OnSessionChanged()
                {
                    if (nav.CurrentRoute is AppRoute.ContainerDetail)
                        LeaveDetail(null);
                }

                AppServices.Current.SessionChanged += OnSessionChanged;
                return () => AppServices.Current.SessionChanged -= OnSessionChanged;
            },
            Array.Empty<object>());

        // The app name is the first item of the content row: WinUI's TitleBar collapses its own title text once it has content.
        var titleBar = (TitleBar("") with
            {
                Content = (FlexRow(
                        Caption("Berth").Foreground(Theme.PrimaryText),
                        ConnectionSwitcher.Render(settings, id => AppServices.Current.SwitchConnection(id)),
                        StatusLabel(state))
                    with
                    {
                        AlignItems = FlexAlign.Center,
                        ColumnGap = 12,
                    }),
            })
            .NoIcon()
            .WithNavigation(nav)
            .PaneToggleButtonVisible(true)
            .PaneToggleRequested(() => setIsPaneOpen(!isPaneOpen))
            .Tall()
            .Flex(shrink: 0);

        var content = FlexColumn(
            SetAsideBar(settingsSetAside, () => setSettingsSetAside(false)),
            ErrorBar.Render(state.ActionError, () => AppServices.Current.Session.Dispatch(new ActionErrorDismissed())),
            NoticeBar(nav.CurrentRoute == AppRoute.Containers ? notice : null, () => setNotice(null)),
            (NavigationHost(nav, route => RouteToPage(route, state, LeaveDetail)) with
                {
                    // The page left stays mounted, so the list keeps its search and scroll while a detail page is open. One is enough:
                    // the list while a detail page shows, the detail page (inactive) while the list shows.
                    CacheMode = NavigationCacheMode.Enabled,
                    CacheSize = 1,
                })
                .Flex(grow: 1, basis: 0));

        var items = new[] { NavItem("Containers", icon: "Library", tag: ContainersTag) };
        var navView = (NavigationView(items, content) with
            {
                // A container's detail page is under Containers.
                SelectedTag = nav.CurrentRoute == AppRoute.Settings ? NavigationViewElement.SettingsTag : ContainersTag,
                // Anything but the containers item is the built-in Settings item, whose reported tag is not always SettingsTag.
                OnSelectedTagChanged = tag => nav.Navigate(tag == ContainersTag ? AppRoute.Containers : AppRoute.Settings),
                IsSettingsVisible = true,
                IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
                IsPaneToggleButtonVisible = false,
            })
            .OpenPaneLength(220) // narrower than the 320 default: the list needs the width more than a short menu does
            .IsPaneOpen(isPaneOpen, setIsPaneOpen)
            .Flex(grow: 1, basis: 0);

        return FlexColumn(titleBar, navView, StatusBar.Render(state));
    }

    /// <summary>Shown from the start until closed when the settings file was damaged and set aside.</summary>
    private static Element? SetAsideBar(bool show, Action onClose) =>
        show
            ? (InfoBar(message: SettingsSetAsideText) with { IsOpen = true })
                .Warning()
                .IsClosable()
                .Closed(onClose)
                .Margin(24, 16, 24, 0)
                .Flex(shrink: 0)
            : null;

    /// <summary>A note for the list, such as that the container whose page was open was removed; shown until closed or the next page opens.</summary>
    private static Element? NoticeBar(string? notice, Action onClose) =>
        notice is null
            ? null
            : (InfoBar(message: notice) with { IsOpen = true })
                .Informational()
                .IsClosable()
                .Closed(onClose)
                .Margin(24, 16, 24, 0)
                .Flex(shrink: 0);

    /// <summary>The connection status; while disconnected, its tooltip gives the reason (the page may still show the last list).</summary>
    private static Element StatusLabel(EngineState state)
    {
        var text = state.Status switch
        {
            ConnectionStatus.Connecting => "Connecting…",
            ConnectionStatus.Connected => "Connected · Docker " + state.Version?.Version,
            _ => "Disconnected",
        };
        var label = Caption(text).Foreground(Theme.SecondaryText);
        return state.Status == ConnectionStatus.Disconnected && state.ConnectionError is { } error ? label.ToolTip(error) : label;
    }

    /// <summary>
    /// The page for <paramref name="route"/>. With no connection and no list from an earlier one, the containers page has nothing to
    /// show and the disconnected view takes its place (with a list, the page keeps it and shows <see cref="ConnectionBar"/> above it);
    /// the settings page stays reachable to fix the connection. A container's page shows its own connection bar.
    /// </summary>
    private static Element RouteToPage(AppRoute route, EngineState state, Action<string?> leaveDetail) => route switch
    {
        AppRoute.ContainerDetail detail => Component<ContainerDetailPage, DetailPageProps>(new(detail.ContainerId, leaveDetail)),
        _ when route == AppRoute.Settings => Component<SettingsPage>(),
        _ when state.Status != ConnectionStatus.Connected && state.Containers.IsEmpty =>
            DisconnectedView.Render(state, () => AppServices.Current.Session.Dispatch(new ConnectRequested())),
        _ => Component<ContainersPage>(),
    };
}

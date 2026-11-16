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

/// <summary>Where the app is: one of the lists (containers, images, volumes, networks), the settings, or one container's detail page.</summary>
internal abstract record AppRoute
{
    public static readonly AppRoute Containers = new Page("containers");

    public static readonly AppRoute Images = new Page("images");

    public static readonly AppRoute Volumes = new Page("volumes");

    public static readonly AppRoute Networks = new Page("networks");

    public static readonly AppRoute Settings = new Page("settings");

    /// <summary>
    /// The detail page of the container <paramref name="ContainerId"/> on the engine session <paramref name="Session"/>
    /// (<see cref="AppServices.SessionNumber"/>); opened from the list, which it returns to. The session number keeps a page kept
    /// from an earlier connection from ever being the one shown again.
    /// </summary>
    public sealed record ContainerDetail(string ContainerId, int Session) : AppRoute;

    private sealed record Page(string Name) : AppRoute;
}

/// <summary>The main window: title bar with the connection switcher, the navigation and its page, the error bar and the status bar.</summary>
internal sealed class App : EngineStateComponent
{
    private const string ContainersTag = "containers";

    /// <summary>
    /// The sidebar: the four lists, each with its route's tag; Settings is the NavigationView's own item at the bottom. Built once:
    /// the App renders on every change of the engine state, and a new item array each time is a new collection for the control.
    /// </summary>
    private static readonly NavigationViewItemData[] NavItems =
    [
        NavItem("Containers", icon: "Library", tag: ContainersTag),
        NavItem("Images", tag: "images") with { IconElement = Widgets.MenuGlyph(Glyphs.Layers) },
        NavItem("Volumes", tag: "volumes") with { IconElement = Widgets.MenuGlyph(Glyphs.HardDrive) },
        NavItem("Networks", tag: "networks") with { IconElement = Widgets.MenuGlyph(Glyphs.Network) },
    ];

    /// <summary>The route of each sidebar item but Settings, by tag.</summary>
    private static readonly IReadOnlyDictionary<string, AppRoute> RoutesByTag = new Dictionary<string, AppRoute>
    {
        [ContainersTag] = AppRoute.Containers,
        ["images"] = AppRoute.Images,
        ["volumes"] = AppRoute.Volumes,
        ["networks"] = AppRoute.Networks,
    };

    /// <summary>The window effect's dependency while the component has no window.</summary>
    private static readonly object NoWindow = new();

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
            if (nav.CurrentRoute is not AppRoute.ContainerDetail)
                return;
            setNotice(note);
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

        // List stats (the containers list's CPU column and totals) are followed while the list is the page shown and the window is
        // not minimized; not on a container's page, which follows its own. The page kept in the navigation cache does not render when
        // another page shows, so the window decides. Sent again to the new session after a switch of connection.
        var minimized = UseMinimized();
        var statsWanted = nav.CurrentRoute == AppRoute.Containers && !minimized;
        UseEffect(
            () => AppServices.Current.Session.Dispatch(new StatsWantedChanged(statsWanted)),
            statsWanted,
            AppServices.Current.SessionNumber);

        // The images, volumes and networks lists are read when their page is first shown, not at connect: each time one of them
        // becomes the page shown, and again for the new session after a switch of connection, the session hears that it is wanted
        // (it reads the list once per connection and ignores the rest).
        UseEffect(
            () =>
            {
                if (WantedEvent(nav.CurrentRoute) is { } wanted)
                    AppServices.Current.Session.Dispatch(wanted);
            },
            nav.CurrentRoute,
            AppServices.Current.SessionNumber);

        // A switch of connection closes the detail session (it ran on the old connection), and no page of a container of the old
        // engine may be shown again: every detail route leaves the history, the one shown for the list. A note about the old engine goes.
        UseEffect(
            () =>
            {
                void OnSessionChanged()
                {
                    setNotice(null);
                    if (WithoutDetailPages(nav.GetState()) is { } history)
                        nav.SetState(history);
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

        // Keyed, so a bar coming or going (the error bar after a failed copy) does not move the pages to another slot and mount them again.
        var content = FlexColumn(
            SetAsideBar(settingsSetAside, () => setSettingsSetAside(false))?.WithKey("set-aside"),
            ErrorBar.Render(state.ActionError, () => AppServices.Current.Session.Dispatch(new ActionErrorDismissed()))?.WithKey("error"),
            NoticeBar(nav.CurrentRoute == AppRoute.Containers ? notice : null, () => setNotice(null))?.WithKey("notice"),
            (NavigationHost(nav, route => RouteToPage(route, state, LeaveDetail)) with
                {
                    // A page left stays mounted, so a list keeps its search, sort and scroll while another page shows. The cache holds
                    // the pages not shown and drops the one used longest ago. There are five fixed pages (four lists and Settings) and
                    // a page per container opened: six hold every list and Settings while a container's page shows, and one more
                    // container's page besides; a list is mounted again only when it was left before three or more container pages.
                    CacheMode = NavigationCacheMode.Enabled,
                    CacheSize = 6,
                })
                .Flex(grow: 1, basis: 0)
                .WithKey("pages"));

        var navView = (NavigationView(NavItems, content) with
            {
                SelectedTag = SelectedTag(nav.CurrentRoute),
                // A tag that is not a list's is the built-in Settings item, whose reported tag is not always SettingsTag.
                // Only a change of item navigates: WinUI also reports the item the route already shows (after Back), which would
                // put the same page on the back stack twice; on a detail page the Containers item is already selected.
                OnSelectedTagChanged = tag =>
                {
                    var target = tag is not null && RoutesByTag.TryGetValue(tag, out var list) ? list : AppRoute.Settings;
                    var shown = nav.CurrentRoute is AppRoute.ContainerDetail ? AppRoute.Containers : nav.CurrentRoute;
                    if (target != shown)
                        nav.Navigate(target);
                },
                IsSettingsVisible = true,
                IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
                IsPaneToggleButtonVisible = false,
            })
            .OpenPaneLength(220) // narrower than the 320 default: the lists need the width more than a short menu does
            .IsPaneOpen(isPaneOpen, setIsPaneOpen)
            .Flex(grow: 1, basis: 0);

        return FlexColumn(titleBar, navView, StatusBar.Render(state));
    }

    /// <summary>The sidebar item of <paramref name="route"/>: a container's detail page is under Containers.</summary>
    private static string SelectedTag(AppRoute route)
    {
        if (route is AppRoute.ContainerDetail)
            return ContainersTag;
        foreach (var (tag, list) in RoutesByTag)
        {
            if (list == route)
                return tag;
        }

        return NavigationViewElement.SettingsTag;
    }

    /// <summary>What the session is told when <paramref name="route"/> becomes the page shown: that its list is wanted; null for the others.</summary>
    private static SessionEvent? WantedEvent(AppRoute route) =>
        route == AppRoute.Images ? new ImagesWanted()
        : route == AppRoute.Volumes ? new VolumesWanted()
        : route == AppRoute.Networks ? new NetworksWanted()
        : null;

    /// <summary>
    /// Whether the window is minimized (iconic). Read again whenever the window changes, shows, hides or is activated: Reactor's
    /// <c>UseWindowState</c> and the presenter's state hear of the minimize but did not change back when the window was restored
    /// from the taskbar.
    /// </summary>
    private bool UseMinimized()
    {
        var window = UseWindow();
        var (minimized, setMinimized) = UseState(false);
        UseEffect(
            () =>
            {
                if (window is null)
                    return () => { };

                var native = window.NativeWindow;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
                void Update() => setMinimized(IsIconic(hwnd));
                void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args) => Update();
                void OnVisibilityChanged(object sender, Microsoft.UI.Xaml.WindowVisibilityChangedEventArgs args) => Update();
                void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args) => Update();
                void OnSizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs args) => Update();

                window.AppWindow.Changed += OnAppWindowChanged;
                native.VisibilityChanged += OnVisibilityChanged;
                native.Activated += OnActivated;
                native.SizeChanged += OnSizeChanged;
                Update();
                return () =>
                {
                    window.AppWindow.Changed -= OnAppWindowChanged;
                    native.VisibilityChanged -= OnVisibilityChanged;
                    native.Activated -= OnActivated;
                    native.SizeChanged -= OnSizeChanged;
                };
            },
            (object?)window ?? NoWindow);
        return minimized;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

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

    /// <summary>
    /// <paramref name="history"/> without its detail pages: the current one becomes the list, the forward stack goes, and a page
    /// left twice in a row on the back stack (or the shown page on top of it) is kept once. Null when there is no detail page in it.
    /// </summary>
    private static NavigationState<AppRoute>? WithoutDetailPages(NavigationState<AppRoute> history)
    {
        static bool IsDetail(AppRoute route) => route is AppRoute.ContainerDetail;
        if (!IsDetail(history.Current) && !history.BackStack.Any(IsDetail) && !history.ForwardStack.Any(IsDetail))
            return null;

        var current = IsDetail(history.Current) ? AppRoute.Containers : history.Current;
        var back = new List<AppRoute>();
        foreach (var route in history.BackStack.Where(r => !IsDetail(r)))
        {
            if (back.Count == 0 || back[^1] != route)
                back.Add(route);
        }

        while (back.Count > 0 && back[^1] == current)
            back.RemoveAt(back.Count - 1);
        return new NavigationState<AppRoute>(back, current, []);
    }

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
    /// The page for <paramref name="route"/>. With no connection and no list from an earlier one, a list page has nothing to show
    /// and the disconnected view takes its place (with a list, the page keeps it and shows <see cref="ConnectionBar"/> above it);
    /// the settings page stays reachable to fix the connection. A container's page shows its own connection bar.
    /// </summary>
    private static Element RouteToPage(AppRoute route, EngineState state, Action<string?> leaveDetail)
    {
        if (route is AppRoute.ContainerDetail detail)
            return Component<ContainerDetailPage, DetailPageProps>(new(detail, leaveDetail));
        if (route == AppRoute.Settings)
            return Component<SettingsPage>();

        var listIsEmpty =
            route == AppRoute.Images ? state.Images.Items.IsEmpty
            : route == AppRoute.Volumes ? state.Volumes.Items.IsEmpty
            : route == AppRoute.Networks ? state.Networks.Items.IsEmpty
            : state.Containers.IsEmpty;
        if (state.Status != ConnectionStatus.Connected && listIsEmpty)
            return DisconnectedView.Render(state, () => AppServices.Current.Session.Dispatch(new ConnectRequested()));

        return route == AppRoute.Images ? Component<ImagesPage>()
            : route == AppRoute.Volumes ? Component<VolumesPage>()
            : route == AppRoute.Networks ? Component<NetworksPage>()
            : Component<ContainersPage>();
    }
}

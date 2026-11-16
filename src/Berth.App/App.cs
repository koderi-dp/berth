using Berth.Components;
using Berth.Hooks;
using Berth.Pages;
using Berth.Presentation;
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

    /// <summary>
    /// The page of the volume <paramref name="Name"/> on the engine session <paramref name="Session"/>; opened from the volumes list,
    /// which it returns to. The session number keeps a page from an earlier connection from being shown again.
    /// </summary>
    public sealed record VolumeDetail(string Name, int Session) : AppRoute;

    /// <summary>Whether <paramref name="route"/> is a detail page (a container's or a volume's), which a switch of connection removes.</summary>
    public static bool IsDetail(AppRoute route) => route is ContainerDetail or VolumeDetail;

    /// <summary>The list a page belongs to: a detail page's list, or the page itself.</summary>
    public static AppRoute ListOf(AppRoute route) => route switch
    {
        ContainerDetail => Containers,
        VolumeDetail => Volumes,
        _ => route,
    };

    /// <summary>
    /// Where a container's page goes back to, from the page it was opened from (<paramref name="before"/>, the route under it in the
    /// history): a volume's page or the Networks list; otherwise the Containers list.
    /// </summary>
    public static BackTarget BackTargetOf(AppRoute? before) => before switch
    {
        VolumeDetail volume => BackTarget.Volume(volume.Name),
        _ when before == Networks => BackTarget.Networks,
        _ => BackTarget.Containers,
    };

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
        // A note for a list ("web-1 was removed."), shown while that list is the page shown.
        var (notice, setNotice) = UseState<(AppRoute List, string Text)?>(null);

        // Back from a detail page, with an optional note for its list. The list is usually the page before; going back to it restores its
        // search, sort and scroll. A container opened from a volume's page or the networks list goes back there, unless it was removed.
        void LeaveDetail(string? note)
        {
            var current = nav.CurrentRoute;
            if (!AppRoute.IsDetail(current))
                return;
            var list = AppRoute.ListOf(current);
            setNotice(note is null ? null : (list, note));
            var before = nav.CanGoBack ? nav.BackStack[^1] : null;
            if (before == list || (note is null && current is AppRoute.ContainerDetail && AppRoute.BackTargetOf(before) != BackTarget.Containers))
                nav.GoBack();
            else
                nav.Replace(list);
        }

        // The detail session belongs to the container's page: none stays open on any other page. A note from the last detail page goes
        // when the next opens.
        UseEffect(
            () =>
            {
                if (AppRoute.IsDetail(nav.CurrentRoute))
                    setNotice(null);
                if (nav.CurrentRoute is not AppRoute.ContainerDetail)
                {
                    AppServices.Current.CloseDetail();
                    Pages.Detail.DetailTabRequest.Clear();
                }
            },
            nav.CurrentRoute);

#if DEBUG
        // BERTH_OPEN_EXEC: the named container's page, on its Exec tab, once its row is listed.
        UseEffect(
            () =>
            {
                if (DebugStart.TakeContainerToOpen(state.Containers) is { } id)
                    nav.Navigate(new AppRoute.ContainerDetail(id, AppServices.Current.SessionNumber));
            },
            state.Containers);
#endif

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
                // The Images and Volumes pages' usage lines and the volumes' sizes: read again each time one is shown (Remove unused names
                // the space it frees from it).
                if (nav.CurrentRoute == AppRoute.Images || AppRoute.ListOf(nav.CurrentRoute) == AppRoute.Volumes)
                    AppServices.Current.Session.Dispatch(new DiskUsageRequested());
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
            NoticeBar(notice is { } shownNotice && nav.CurrentRoute == shownNotice.List ? shownNotice.Text : null, () => setNotice(null))?.WithKey("notice"),
            // Each route's page is one RoutePage component, whatever it shows: when a page's root changed type (a list and the
            // disconnected view in its place, as on every switch of connection), the host kept the old page alive with its whole tree.
            (NavigationHost(nav, route => Component<RoutePage, RoutePageProps>(new(route, LeaveDetail))) with
                {
                    // A page left stays mounted, so a list keeps its search, sort and scroll while another page shows. The cache holds
                    // the pages not shown and drops the one used longest ago. There are five fixed pages (four lists and Settings) and
                    // a page per container opened: six hold every list and Settings while a container's page shows, and one more
                    // container's page besides. A list left before more container pages were opened is mounted again and starts from
                    // its search, filter and sort kept by route (ListPageComponent.UseListView); only its scroll and checked rows go.
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
                    var shown = AppRoute.ListOf(nav.CurrentRoute);
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

        // The app's shortcuts sit at the root, so they work wherever the focus is, and act for the page shown (ShellKeys).
        return FlexColumn(titleBar, navView, StatusBar.Render(state)).OnMount(root => ShellKeys.Attach(root, nav));
    }

    /// <summary>The sidebar item of <paramref name="route"/>: a container's detail page is under Containers.</summary>
    private static string SelectedTag(AppRoute route)
    {
        route = AppRoute.ListOf(route);
        foreach (var (tag, list) in RoutesByTag)
        {
            if (list == route)
                return tag;
        }

        return NavigationViewElement.SettingsTag;
    }

    /// <summary>What the session is told when <paramref name="route"/> becomes the page shown: that its list is wanted; null for the others.</summary>
    private static SessionEvent? WantedEvent(AppRoute route) =>
        AppRoute.ListOf(route) == AppRoute.Volumes ? new VolumesWanted()
        : route == AppRoute.Images ? new ImagesWanted()
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
        static bool IsDetail(AppRoute route) => AppRoute.IsDetail(route);
        if (!IsDetail(history.Current) && !history.BackStack.Any(IsDetail) && !history.ForwardStack.Any(IsDetail))
            return null;

        var current = AppRoute.ListOf(history.Current);
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
    internal static Element RouteToPage(AppRoute route, EngineState state, Action<string?> leaveDetail)
    {
        if (route is AppRoute.ContainerDetail detail)
            return Component<ContainerDetailPage, DetailPageProps>(new(detail, leaveDetail));
        if (route is AppRoute.VolumeDetail volume)
        {
            return KeptMounted(
                Component<VolumeDetailPage, VolumeDetailProps>(new(volume, leaveDetail)),
                state,
                state.Status != ConnectionStatus.Connected && state.Volumes.Items.IsEmpty);
        }
        if (route == AppRoute.Settings)
            return Component<SettingsPage>();

        var listIsEmpty =
            route == AppRoute.Images ? state.Images.Items.IsEmpty
            : route == AppRoute.Volumes ? state.Volumes.Items.IsEmpty
            : route == AppRoute.Networks ? state.Networks.Items.IsEmpty
            : state.Containers.IsEmpty;
        var page = route == AppRoute.Images ? Component<ImagesPage>()
            : route == AppRoute.Volumes ? Component<VolumesPage>()
            : route == AppRoute.Networks ? Component<NetworksPage>()
            : Component<ContainersPage>();
        return KeptMounted(page, state, state.Status != ConnectionStatus.Connected && listIsEmpty);
    }

    private static readonly GridSize[] OneCell = [GridSize.Star()];

    /// <summary>
    /// <paramref name="page"/>, hidden under the disconnected view while <paramref name="disconnected"/>, but never unmounted for it: a
    /// list page taken out of the tree on every switch of connection (the new session starts with no list) was never freed, since the
    /// components in its virtualized rows stayed registered with Reactor (about 0.7 MB a switch).
    /// </summary>
    private static Element KeptMounted(Element page, EngineState state, bool disconnected) =>
        Grid(
            OneCell,
            OneCell,
            page.IsVisible(!disconnected).WithKey("page"),
            disconnected ? DisconnectedView.Render(state, () => AppServices.Current.Session.Dispatch(new ConnectRequested())).WithKey("disconnected") : null);
}

/// <param name="Route">The route the navigation host shows.</param>
/// <param name="LeaveDetail">What a detail page calls to go back to its list, with an optional note for it.</param>
internal sealed record RoutePageProps(AppRoute Route, Action<string?> LeaveDetail);

/// <summary>
/// The page of one route (<see cref="App.RouteToPage"/>), so the navigation host's root for a route is always this component. The host
/// keeps a page whose root element changes type, and every element under it, alive for good: the Containers list and the disconnected
/// view swapped places on each switch of connection, and each switch kept one more page with its native controls (about 17 MB).
/// </summary>
internal sealed class RoutePage : EngineStateComponent<RoutePageProps>
{
    public override Element Render() => App.RouteToPage(Props.Route, UseEngineState(), Props.LeaveDetail);
}

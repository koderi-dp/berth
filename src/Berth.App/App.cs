using Berth.Components;
using Berth.Hooks;
using Berth.Pages;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth;

internal enum AppRoute { Containers, Settings }

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
            NavigationHost(nav, route => RouteToPage(route, state)).Flex(grow: 1, basis: 0));

        var items = new[] { NavItem("Containers", icon: "Library", tag: ContainersTag) };
        var navView = (NavigationView(items, content) with
            {
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
    /// the settings page stays reachable to fix the connection.
    /// </summary>
    private static Element RouteToPage(AppRoute route, EngineState state) => route switch
    {
        AppRoute.Settings => Component<SettingsPage>(),
        _ when state.Status != ConnectionStatus.Connected && state.Containers.IsEmpty =>
            DisconnectedView.Render(state, () => AppServices.Current.Session.Dispatch(new ConnectRequested())),
        _ => Component<ContainersPage>(),
    };
}

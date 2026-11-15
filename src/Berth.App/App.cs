using Berth.Components;
using Berth.Hooks;
using Berth.Pages;
using Berth.Session;
using Berth.Settings;
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

    private static readonly Func<Action, Action> SubscribeSettings = onChange =>
    {
        var services = AppServices.Current;
        services.SettingsChanged += onChange;
        return () => services.SettingsChanged -= onChange;
    };

    private static readonly Func<AppSettings> SettingsSnapshot = () => AppServices.Current.Settings;

    public override Element Render()
    {
        var state = UseEngineState();
        var settings = UseExternalStore(SubscribeSettings, SettingsSnapshot);
        var nav = UseNavigation(AppRoute.Containers);
        var (isPaneOpen, setIsPaneOpen) = UseState(true);

        var titleBar = (TitleBar("Berth") with
            {
                Content = (FlexRow(
                        ConnectionSwitcher.Render(settings, id => AppServices.Current.SwitchConnection(id)),
                        StatusLabel(state))
                    with
                    {
                        AlignItems = FlexAlign.Center,
                        ColumnGap = 12,
                    }),
            })
            .WithNavigation(nav)
            .PaneToggleButtonVisible(true)
            .PaneToggleRequested(() => setIsPaneOpen(!isPaneOpen))
            .Tall()
            .Flex(shrink: 0);

        var content = FlexColumn(
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
            .IsPaneOpen(isPaneOpen, setIsPaneOpen)
            .Flex(grow: 1, basis: 0);

        return FlexColumn(titleBar, navView, StatusBar.Render(state));
    }

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
    /// show and the disconnected view takes its place; the settings page stays reachable to fix the connection.
    /// </summary>
    private static Element RouteToPage(AppRoute route, EngineState state) => route switch
    {
        AppRoute.Settings => Component<SettingsPage>(),
        _ when state.Status != ConnectionStatus.Connected && state.Containers.IsEmpty =>
            DisconnectedView.Render(state, () => AppServices.Current.Session.Dispatch(new ConnectRequested())),
        _ => Component<ContainersPage>(),
    };
}

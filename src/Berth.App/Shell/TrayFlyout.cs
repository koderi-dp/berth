using Berth.Components;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Shell;

/// <summary>
/// The tray icon's menu, shown in Reactor's tray flyout (a small window at the pointer that closes when it loses the focus): Open
/// Berth; the connection and its state; the running containers with Stop and Restart; Start engine or Stop engine (a stop asks first,
/// in the menu, Cancel first); Quit. Built from <see cref="TrayIcon.Menu"/>, again only when that changes.
/// </summary>
internal sealed class TrayFlyout : Component
{
    private static readonly Func<Action, Action> Subscribe = onChange =>
    {
        TrayIcon.MenuChanged += onChange;
        return () => TrayIcon.MenuChanged -= onChange;
    };

    private static readonly Func<TrayMenu> Snapshot = () => TrayIcon.Menu;

    private static readonly GridSize[] ContainerColumns = [GridSize.Star(), GridSize.Auto, GridSize.Auto];
    private static readonly GridSize[] OneRow = [GridSize.Auto];

    private const double MenuWidth = 320;

    public override Element Render()
    {
        var menu = UseExternalStore(Subscribe, Snapshot);
        var (confirmStop, setConfirmStop) = UseState(false);
        var items = UseMemo(() => Items(menu, confirmStop, setConfirmStop), menu, confirmStop);

        // Sized by its content, at the top left, so the window can take its size (TrayFlyoutWindow).
        return Border(VStack(2, items))
            .Padding(4)
            .Width(MenuWidth)
            .HAlign(HorizontalAlignment.Left)
            .VAlign(VerticalAlignment.Top)
            .Background(Theme.Ref("SolidBackgroundFillColorBaseBrush"))
            .AutomationName("Berth menu")
            .AutomationId("tray-menu")
            .OnMount(TrayFlyoutWindow.Fit);
    }

    private static Element[] Items(TrayMenu menu, bool confirmStop, Action<bool> setConfirmStop)
    {
        var items = new List<Element>
        {
            Item("Open Berth", Glyphs.OpenInNewWindow, OpenBerth, "tray-open"),
            Divider(),
            Header(menu),
        };

        foreach (var container in menu.Containers)
            items.Add(ContainerRow(container));
        if (menu.MoreText is { } more)
            items.Add(Item(more, null, OpenBerth, "tray-more"));

        if (menu.Engine is { } engine)
        {
            items.Add(Divider());
            if (confirmStop && engine.Command == EngineCommand.Stop)
                items.Add(StopQuestion(setConfirmStop));
            else if (engine.Command == EngineCommand.Start)
                items.Add(Item(engine.Label, Glyphs.Play, StartEngine, "tray-start-engine").IsEnabled(engine.Enabled));
            else
                items.Add(Item(engine.Label, Glyphs.Stop, () => setConfirmStop(true), "tray-stop-engine").IsEnabled(engine.Enabled));
        }

        items.Add(Divider());
        items.Add(Item("Quit Berth", Glyphs.Close, Quit, "tray-quit"));
        return [.. items];
    }

    private static Element Header(TrayMenu menu) =>
        VStack(
                2,
                Widgets.OneLine(Widgets.StrongText(menu.ConnectionName)).ToolTip(menu.ConnectionName),
                HStack(
                    8,
                    Widgets.StatusDot(StatusBar.ToneColor(ToneOf(menu.Icon))).VAlign(VerticalAlignment.Center),
                    Caption(menu.StateText).Foreground(Theme.SecondaryText)))
            .Margin(12, 4, 12, 4)
            .AutomationId("tray-state")
            .AutomationName(menu.ConnectionName + ": " + menu.StateText);

    private static Element ContainerRow(TrayContainer container) =>
        Grid(
                ContainerColumns,
                OneRow,
                Widgets.OneLine(Body(container.Name)).ToolTip(container.Name).VAlign(VerticalAlignment.Center).Margin(12, 0, 8, 0),
                Widgets.IconButton(Glyphs.Stop, "Stop " + container.Name, () => TrayIcon.Act(container.Id, ContainerAction.Stop))
                    .IsEnabled(!container.Busy)
                    .AutomationId("tray-stop-" + container.Name)
                    .Grid(row: 0, column: 1),
                Widgets.IconButton(Glyphs.Restart, "Restart " + container.Name, () => TrayIcon.Act(container.Id, ContainerAction.Restart))
                    .IsEnabled(!container.Busy)
                    .AutomationId("tray-restart-" + container.Name)
                    .Grid(row: 0, column: 2))
            .WithKey(container.Id);

    /// <summary>The stop question in the menu itself, naming the containers that will stop; Cancel is first and the main button.</summary>
    private static Element StopQuestion(Action<bool> setConfirmStop) =>
        VStack(
                8,
                Body(EngineStatus.StopConfirmation(AppServices.Current.Session.State)).TextWrapping().AutomationId("tray-stop-question"),
                HStack(
                    8,
                    Button("Cancel", () => setConfirmStop(false)).AccentButton().AutomationId("tray-stop-cancel"),
                    Button("Stop engine", () =>
                    {
                        setConfirmStop(false);
                        AppServices.Current.Session.Dispatch(new EngineCommandRequested(EngineCommand.Stop));
                    }).AutomationId("tray-stop-confirm")))
            .Margin(12, 4, 12, 4);

    private static ButtonElement Item(string label, string? glyph, Action onClick, string automationId) =>
        Button(HStack(12, glyph is null ? Empty().Width(16) : Widgets.Glyph(glyph), Body(label)), onClick)
            .SubtleButton()
            .HAlign(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Left)
            .AutomationName(label)
            .AutomationId(automationId);

    private static Element Divider() =>
        Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(0, 4, 0, 4);

    private static StatusTone ToneOf(TrayIconKind kind) => kind switch
    {
        TrayIconKind.Running => StatusTone.Success,
        TrayIconKind.Down => StatusTone.Critical,
        TrayIconKind.Stopped => StatusTone.Neutral,
        _ => StatusTone.Caution,
    };

    private static void OpenBerth()
    {
        TrayIcon.HideMenu();
        MainWindow.Show();
    }

    private static void StartEngine()
    {
        TrayIcon.HideMenu();
        AppServices.Current.Session.Dispatch(new EngineCommandRequested(EngineCommand.Start));
    }

    private static void Quit()
    {
        TrayIcon.HideMenu();
        MainWindow.Quit();
    }
}

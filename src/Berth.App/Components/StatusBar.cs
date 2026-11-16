using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using MenuEntry = Microsoft.UI.Reactor.Core.MenuFlyoutItemBase;

namespace Berth.Components;

/// <summary>
/// The strip along the bottom of the window: the engine's state (<see cref="EngineStatus"/>), then its container counts. With engine
/// control the state is a button whose menu has Start engine or Stop engine; a stop asks first (<see cref="EngineStopDialog"/>).
/// </summary>
internal sealed class StatusBar : EngineStateComponent
{
    public override Element Render()
    {
        var state = UseEngineState();
        var (confirmStop, setConfirmStop) = UseState(false);
        var menu = EngineStatus.Menu(state);
        // Built again only when what it offers changes: the bar renders on every change of the engine state.
        var items = UseMemo(() => menu is null ? null : MenuEntries(menu, () => setConfirmStop(true)), menu);

        var phase = EngineStatus.Phase(state);
        var text = EngineStatus.Text(phase);
        var counts = state.Status == ConnectionStatus.Connected && state.Info is { } info
            ? Caption(EngineCounts.Text(info)).Foreground(Theme.SecondaryText)
            : null;

        var label = (FlexRow(Widgets.StatusDot(ToneColor(EngineStatus.Tone(phase))), Caption(text).Foreground(Theme.PrimaryText)) with
        {
            AlignItems = FlexAlign.Center,
            ColumnGap = 8,
        });

        Element engine = items is null
            ? label.AutomationName(text).AutomationId("engine-status")
            : Button(label)
                .WithFlyout(MenuItems(items))
                .AutomationName(text + ". Engine actions")
                .AutomationId("engine-status")
                .Padding(8, 2)
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .BorderThickness(0);

        var row = (FlexRow(engine, counts) with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 8,
            })
            .FlexPadding(items is null ? 16 : 8, items is null ? 6 : 2, 16, items is null ? 6 : 2);

        return FlexColumn(
                Border(row)
                    .BorderBrush(Theme.DividerStroke)
                    .BorderThickness(0, 1, 0, 0),
                EngineStopDialog.Render(confirmStop ? state : null, () => setConfirmStop(false)))
            .Flex(shrink: 0);
    }

    internal static ThemeRef ToneColor(StatusTone tone) => tone switch
    {
        StatusTone.Success => Theme.SystemSuccess,
        StatusTone.Caution => Theme.SystemCaution,
        StatusTone.Critical => Theme.SystemCritical,
        _ => Theme.SecondaryText,
    };

    private static MenuEntry[] MenuEntries(EngineMenu menu, Action askStop) =>
    [
        MenuItem(menu.Label, menu.Command == EngineCommand.Start ? StartEngine : askStop) with
        {
            IconElement = Widgets.MenuGlyph(menu.Command == EngineCommand.Start ? Glyphs.Play : Glyphs.Stop),
            IsEnabled = menu.Enabled,
        },
    ];

    private static void StartEngine() => AppServices.Current.Session.Dispatch(new EngineCommandRequested(EngineCommand.Start));
}

/// <summary>The question before the engine is stopped: names the running containers that will stop, and defaults to Cancel.</summary>
internal static class EngineStopDialog
{
    /// <param name="state">The engine state while the dialog is open; null while it is closed.</param>
    public static Element Render(EngineState? state, Action onDone) =>
        ContentDialog(EngineStatus.StopTitle, Body(state is null ? "" : EngineStatus.StopConfirmation(state)).TextWrapping().AutomationId("confirm-text"), "Stop engine") with
        {
            IsOpen = state is not null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            OnClosed = result =>
            {
                if (result == ContentDialogResult.Primary)
                    AppServices.Current.Session.Dispatch(new EngineCommandRequested(EngineCommand.Stop));
                onDone();
            },
        };
}

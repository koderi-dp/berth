using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>
/// Shown in place of the page while there is no connection and nothing to show from an earlier one: why the engine could not be
/// reached, in the words the engine or the socket used, and a way to try again; with engine control, "Start engine", its progress,
/// and a failed command's output as it wrote it (<see cref="EngineStatus.Disconnected"/>).
/// </summary>
internal static class DisconnectedView
{
    public static Element Render(EngineState state, Action onRetry)
    {
        var view = EngineStatus.Disconnected(state);

        return (FlexColumn(
                view.Busy ? ProgressRing().Size(32, 32).AutomationName(view.Title) : null,
                Widgets.SectionTitle(view.Title).AutomationId("disconnected-title").LiveRegion(AutomationLiveSetting.Polite),
                view.Message is { } message
                    ? Body(message).IsTextSelectionEnabled().TextWrapping().TextAlignment(TextAlignment.Center).Foreground(Theme.SecondaryText)
                    : null,
                view.Failure is { } failure ? FailureText(failure) : null,
                Buttons(view, onRetry))
            with
            {
                AlignItems = FlexAlign.Center,
                JustifyContent = FlexJustify.Center,
                RowGap = 16,
            })
            .FlexPadding(24)
            .Flex(grow: 1, basis: 0);
    }

    /// <summary>"Start engine" (the main action when offered) and Retry; while something is under way, Retry only, disabled.</summary>
    private static Element Buttons(DisconnectedText view, Action onRetry) =>
        (FlexRow(
                view.OfferStart
                    ? Button("Start engine", StartEngine).AccentButton().AutomationId("start-engine")
                    : null,
                Button("Retry", onRetry).IsEnabled(view.CanRetry).AutomationId("retry-disconnected"))
            with
            {
                ColumnGap = 8,
            });

    /// <summary>A failed start or stop: what failed, then its output as the command wrote it, selectable, in the monospace font.</summary>
    internal static Element FailureText(string failure) =>
        Border(
                TextBlock(failure)
                    .FontFamily("Cascadia Mono, Consolas")
                    .IsTextSelectionEnabled()
                    .TextWrapping()
                    .Foreground(Theme.PrimaryText)
                    .AutomationId("engine-command-failure")
                    .LiveRegion(AutomationLiveSetting.Assertive))
            .Background(Theme.Ref("SystemFillColorCriticalBackgroundBrush"))
            .CornerRadius(4)
            .Padding(12, 8)
            .MaxWidth(720);

    private static void StartEngine() => AppServices.Current.Session.Dispatch(new EngineCommandRequested(EngineCommand.Start));
}

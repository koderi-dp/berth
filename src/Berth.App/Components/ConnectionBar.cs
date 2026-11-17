using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>
/// Shown above a list kept from an earlier connection while the engine is not connected: why, in the words the engine or the
/// socket used (or that a connect, a start or a stop is under way, or that the engine was stopped from Berth), and a way to try again
/// now instead of waiting for the next retry; with engine control, "Start engine".
/// </summary>
internal static class ConnectionBar
{
    public static Element? Render(EngineState state, Action onRetry)
    {
        if (state.Status == ConnectionStatus.Connected)
            return null;

        var view = EngineStatus.Disconnected(state);
        var message = view.Failure ?? view.Message ?? (view.Busy ? EngineStatus.Text(EngineStatus.Phase(state)) : view.Title);

        var bar = (InfoBar(message: message) with
            {
                IsOpen = true,
                Content = (FlexRow(
                        view.Prompt is not null && state.Attention is { } problem
                            ? Component<SshPromptButton, SshPromptProps>(new(problem)).WithKey("ssh-prompt")
                            : null,
                        view.OfferStart
                            ? Button("Start engine", StartEngine).AccentButton().AutomationId("start-engine-bar")
                            : null,
                        Button("Retry", onRetry)
                            .AutomationName("Retry")
                            .AutomationId("retry-connection")
                            .IsEnabled(view.CanRetry))
                    with
                    {
                        ColumnGap = 8,
                    })
                    .Margin(0, 0, 0, 12),
            })
            .IsClosable(false)
            .Flex(shrink: 0);
        // Stopped from Berth is not a fault: said plainly, not as a warning.
        return EngineStatus.Phase(state) == EnginePhase.Stopped && view.Failure is null ? bar.Informational() : bar.Warning();
    }

    private static void StartEngine() => AppServices.Current.Session.Dispatch(new EngineCommandRequested(EngineCommand.Start));
}

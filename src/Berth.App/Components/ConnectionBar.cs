using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>
/// Shown above a list kept from an earlier connection while the engine is not connected: why, in the words the engine or the
/// socket used (or that a connect is under way), and a way to try again now instead of waiting for the next retry.
/// </summary>
internal static class ConnectionBar
{
    public static Element? Render(EngineState state, Action onRetry)
    {
        if (state.Status == ConnectionStatus.Connected)
            return null;

        var connecting = state.Status == ConnectionStatus.Connecting;
        var message = connecting ? "Connecting…" : state.ConnectionError ?? "Not connected to the engine.";

        return (InfoBar(message: message) with
            {
                IsOpen = true,
                Content = Button("Retry", onRetry)
                    .AutomationName("Retry")
                    .AutomationId("retry-connection")
                    .IsEnabled(!connecting)
                    .Margin(0, 0, 0, 12),
            })
            .Warning()
            .IsClosable(false)
            .Flex(shrink: 0);
    }
}

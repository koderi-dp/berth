using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>
/// Shown in place of the page while there is no connection and nothing to show from an earlier one: why the engine could not be
/// reached, in the words the engine or the socket used, and a way to try again.
/// </summary>
internal static class DisconnectedView
{
    public static Element Render(EngineState state, Action onRetry)
    {
        var connecting = state.Status == ConnectionStatus.Connecting;

        return (FlexColumn(
                connecting ? ProgressRing().Size(32, 32) : null,
                Widgets.SectionTitle(connecting ? "Connecting to the engine" : "Not connected to the engine"),
                state.ConnectionError is { } error
                    ? Body(error).IsTextSelectionEnabled().TextWrapping().TextAlignment(TextAlignment.Center).Foreground(Theme.SecondaryText)
                    : null,
                Button("Retry", onRetry).IsEnabled(!connecting))
            with
            {
                AlignItems = FlexAlign.Center,
                JustifyContent = FlexJustify.Center,
                RowGap = 16,
            })
            .FlexPadding(24)
            .Flex(grow: 1, basis: 0);
    }
}

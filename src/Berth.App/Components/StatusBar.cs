using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>The strip along the bottom of the window: whether the engine is reachable, then its container counts.</summary>
internal static class StatusBar
{
    public static Element Render(EngineState state)
    {
        var connected = state.Status == ConnectionStatus.Connected;
        var counts = connected && state.Info is { } info
            ? Caption(EngineCounts.Text(info)).Foreground(Theme.SecondaryText)
            : null;
        var (color, text) = state.Status switch
        {
            ConnectionStatus.Connected => (Theme.SystemSuccess, "Engine running"),
            ConnectionStatus.Connecting => (Theme.SystemCaution, "Connecting…"),
            _ => (Theme.SystemCritical, "Engine not reachable"),
        };

        var row = (FlexRow(
                Widgets.StatusDot(color),
                Caption(text).Foreground(Theme.PrimaryText),
                counts)
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 8,
            })
            .FlexPadding(16, 6, 16, 6);

        return Border(row)
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 1, 0, 0)
            .Flex(shrink: 0);
    }
}

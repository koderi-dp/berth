using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>The last failed action, as the engine reported it, until the user closes it.</summary>
internal static class ErrorBar
{
    public static Element? Render(string? actionError, Action onDismiss) =>
        actionError is null
            ? null
            : (InfoBar(message: actionError) with { IsOpen = true })
                .Error()
                .IsClosable()
                .Closed(onDismiss)
                .Margin(24, 16, 24, 0)
                .Flex(shrink: 0);
}

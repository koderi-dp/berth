using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>Small pieces shared by the shell and the pages.</summary>
internal static class Widgets
{
    /// <summary>A round state dot in a theme colour (the <c>Theme.System*</c> signal tokens).</summary>
    public static Element StatusDot(ThemeRef color) =>
        Border(Empty())
            .Background(color)
            .CornerRadius(4)
            .Size(8, 8);
}

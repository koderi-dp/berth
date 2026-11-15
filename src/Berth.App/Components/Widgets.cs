using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>Segoe Fluent Icons glyphs the app uses.</summary>
internal static class Glyphs
{
    public const string Play = "";
    public const string Stop = "";
    public const string More = "";
    public const string Delete = "";
    public const string Edit = "";
    public const string Copy = "";
    public const string ChevronDown = "";
    public const string ChevronRight = "";
}

/// <summary>Small pieces shared by the shell and the pages.</summary>
internal static class Widgets
{
    private const string IconFont = "Segoe Fluent Icons";

    /// <summary>A round state dot in a theme colour (the <c>Theme.System*</c> signal tokens).</summary>
    public static Element StatusDot(ThemeRef color) =>
        Border(Empty())
            .Background(color)
            .CornerRadius(4)
            .Size(8, 8);

    /// <summary>A Segoe Fluent Icons glyph; it takes the colour of the control it sits in.</summary>
    public static Element Glyph(string glyph, double size = 16) => Icon(FontIcon(glyph, IconFont, size));

    /// <summary>
    /// A borderless button that shows only <paramref name="glyph"/>; <paramref name="label"/> is its accessible name and its tooltip.
    /// </summary>
    public static ButtonElement IconButton(string glyph, string label, Action? onClick, double size = 16) =>
        Button(Glyph(glyph, size), onClick)
            .SubtleButton()
            .AutomationName(label)
            .ToolTip(label);
}

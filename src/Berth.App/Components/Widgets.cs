using Microsoft.UI.Reactor;
using Microsoft.UI.Text;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>Segoe Fluent Icons glyphs the app uses.</summary>
internal static class Glyphs
{
    public const string Play = ""; // PlaySolid
    public const string Stop = ""; // StopSolid: the outline Stop reads as an empty checkbox next to the row checkboxes
    public const string Restart = ""; // Refresh
    public const string Pause = "";
    public const string Resume = ""; // the outline Play: Start is the solid one
    public const string More = "";
    public const string Delete = "";
    public const string Edit = "";
    public const string Copy = "";
    public const string Globe = "";
    public const string ChevronDown = "";
    public const string ArrowUp = "";
    public const string ArrowDown = "";
    public const string ChevronLeft = "";
    public const string Details = ""; // Info
    public const string ChevronRight = "";
    public const string ChevronUp = "";
    public const string Search = "";
    public const string Clock = "";
    public const string Wrap = ""; // ReturnKey: the arrow that carries a line on to the next
    public const string Erase = ""; // EraseTool
    public const string Close = ""; // Cancel
}

/// <summary>Small pieces shared by the shell and the pages.</summary>
internal static class Widgets
{
    private const string IconFont = "Segoe Fluent Icons";

    /// <summary>
    /// Text that keeps the theme's primary text colour when the theme changes while Berth runs. A TextBlock without a Foreground
    /// inherits one that WinUI does not update when an ancestor's theme returns to the system's (it stays the colour of the theme
    /// before); a ThemeRef Foreground follows. Reactor applies that Foreground as a style of its own, which replaces the WinUI text
    /// style of <c>Title</c>, <c>Subtitle</c> and <c>BodyStrong</c>, so these set the style's size and weight themselves.
    /// </summary>
    public static TextBlockElement PageTitle(string text) => Title(text).Foreground(Theme.PrimaryText).FontSize(28).FontWeight(FontWeights.SemiBold);

    /// <inheritdoc cref="PageTitle"/>
    public static TextBlockElement SectionTitle(string text) => Subtitle(text).Foreground(Theme.PrimaryText).FontSize(20).FontWeight(FontWeights.SemiBold);

    /// <inheritdoc cref="PageTitle"/>
    public static TextBlockElement StrongText(string text) => BodyStrong(text).Foreground(Theme.PrimaryText).FontSize(14).FontWeight(FontWeights.SemiBold);

    /// <summary>A round state dot in a theme colour (the <c>Theme.System*</c> signal tokens).</summary>
    public static Element StatusDot(ThemeRef color) =>
        Border(Empty())
            .Background(color)
            .CornerRadius(4)
            .Size(8, 8);

    /// <summary>
    /// A container's or project's state as a 12 pixel shape in <paramref name="color"/>: a filled dot, a ring, or a ring whose left
    /// half is filled. The shape alone tells the states apart, for anyone who cannot tell the colours apart.
    /// </summary>
    public static Element StateMark(Berth.Presentation.StateShape shape, ThemeRef color) => shape switch
    {
        Berth.Presentation.StateShape.Filled => Border(Empty()).Background(color).CornerRadius(6).Size(12, 12),
        Berth.Presentation.StateShape.Half => Border(
                Border(Empty()).Background(color).CornerRadius(4, 0, 0, 4).Width(4).HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Left))
            .BorderBrush(color).BorderThickness(2, 2, 2, 2).CornerRadius(6).Size(12, 12),
        _ => Border(Empty()).BorderBrush(color).BorderThickness(2, 2, 2, 2).CornerRadius(6).Size(12, 12),
    };

    /// <summary>
    /// A Segoe Fluent Icons glyph. Without a <paramref name="color"/> it takes the colour of the control it sits in (and so dims
    /// when that is disabled); with one it is drawn in it.
    /// </summary>
    public static Element Glyph(string glyph, double size = 16, ThemeRef? color = null) =>
        color is { } drawn
            ? TextBlock(glyph).FontFamily(IconFont).FontSize(size).Foreground(drawn)
            : Icon(FontIcon(glyph, IconFont, size));

    /// <summary>A Segoe Fluent Icons glyph as a menu item's icon, so a menu shows the same icon as the button for the action.</summary>
    public static IconData MenuGlyph(string glyph) => FontIcon(glyph, IconFont);

    /// <summary>A short note centred in the room it is given, in the secondary text colour.</summary>
    public static Element CenteredNote(string text) =>
        Body(text)
            .Foreground(Theme.SecondaryText)
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Center)
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

    /// <summary>
    /// A borderless button that shows only <paramref name="glyph"/>; <paramref name="label"/> is its accessible name and its tooltip.
    /// Pass a <paramref name="color"/> only while the button is enabled: a coloured glyph does not dim.
    /// </summary>
    public static ButtonElement IconButton(string glyph, string label, Action? onClick, double size = 16, ThemeRef? color = null) =>
        Button(Glyph(glyph, size, color), onClick)
            .SubtleButton()
            .AutomationName(label)
            .ToolTip(label);
}

using Microsoft.UI.Reactor;
using Microsoft.UI.Text;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
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
    public const string Palette = "\uE790"; // Color: a log's colours
    public const string Erase = ""; // EraseTool
    public const string Close = ""; // Cancel
    public const string CommandPrompt = "";
    public const string OpenFolder = ""; // OpenLocal
    public const string Layers = ""; // MapLayers: an image is a stack of layers
    public const string Download = "";
    public const string Code = ""; // the raw JSON of an image's Inspect
    public const string Add = "";
    public const string HardDrive = "";
    public const string Network = "";
    public const string OpenInNewWindow = "";
    public const string Folder = "\uE8B7";
    public const string Document = "\uE8A5";
    public const string Link = "\uE71B";
    public const string Special = "\uE9CE"; // Unknown: a device, FIFO or socket
    public const string Up = "\uE74A";
    public const string Upload = "\uE898";
    public const string Logs = "\uE8FD"; // BulletedList: lines of text
    public const string Filter = "\uE71C";
    public const string Save = "\uE74E";
    public const string Columns = "\uE8A9"; // ViewAll: which columns show
    public const string Container = "\uE7B8"; // Package: a container
    public const string Project = "\uE8F1"; // Library: a compose project's containers together
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

    /// <summary>
    /// A text box for every text field in the app. It is uncontrolled: it owns its text, starts from <paramref name="value"/> when it
    /// is mounted and reports every change to <paramref name="onChanged"/>; nothing writes over it afterwards. A controlled box, whose
    /// text Reactor writes, drops the first edit after it mounts and after its page comes back from the navigation cache: Reactor
    /// takes that change for the echo of its own write and puts the old text back. Setting the text on mount reports the same value
    /// back, which changes nothing.
    /// </summary>
    public static TextBoxElement TextField(string value, Action<string> onChanged, string? placeholder = null, string? header = null) =>
        TextBox(onChanged: onChanged, placeholderText: placeholder, header: header)
            .OnMount(control => StartFrom(control, value));

    /// <summary>
    /// A search box: a <see cref="TextField"/> that tells <paramref name="onFocusChanged"/> when it gets and loses the focus and is
    /// reached through <paramref name="box"/> (to focus it, or to empty it). With <paramref name="clearOnEscape"/>, Escape in a box
    /// that has text empties it (which reports the empty text). Every other key goes to <paramref name="onKeyDown"/>.
    /// </summary>
    public static TextBoxElement SearchBox(
        string value,
        Action<string> onChanged,
        string placeholder,
        ElementRef<TextBox> box,
        Action<bool> onFocusChanged,
        bool clearOnEscape = false,
        Action<KeyRoutedEventArgs>? onKeyDown = null)
    {
        var field = TextField(value, onChanged, placeholder)
            .Ref(box)
            .OnGotFocus((_, _) => onFocusChanged(true))
            .OnLostFocus((_, _) => onFocusChanged(false));
        if (!clearOnEscape && onKeyDown is null)
            return field;

        return field.OnKeyDown((sender, e) =>
        {
            if (clearOnEscape && e.Key == VirtualKey.Escape && sender is TextBox { Text.Length: > 0 } text)
            {
                text.Text = "";
                e.Handled = true;
                return;
            }

            onKeyDown?.Invoke(e);
        });
    }

    /// <summary>One line of text that ends in an ellipsis when it does not fit (the body styles wrap by default).</summary>
    public static TextBlockElement OneLine(TextBlockElement text) =>
        text.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis);

    /// <summary>
    /// An error bar that cannot be closed: <paramref name="message"/> as the engine or the socket gave it, and a Retry button that
    /// calls <paramref name="onRetry"/> (none when it is null).
    /// </summary>
    public static InfoBarElement RetryBar(string message, Action? onRetry)
    {
        var bar = InfoBar(message: message) with { IsOpen = true };
        if (onRetry is not null)
            bar = bar with { ActionButtonContent = "Retry", OnActionButtonClick = onRetry };
        return bar.Error().IsClosable(false);
    }

    private static void StartFrom(FrameworkElement control, string value)
    {
        if (control is TextBox box && box.Text != value)
            box.Text = value;
    }
}

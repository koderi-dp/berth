using Berth.Engine.Models;
using Berth.Presentation;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Run = Microsoft.UI.Xaml.Documents.Run;
using TextHighlighter = Microsoft.UI.Xaml.Documents.TextHighlighter;
using TextRange = Microsoft.UI.Xaml.Documents.TextRange;

namespace Berth.Components;

/// <summary>
/// How a log line is drawn in a TextBlock, shared by a container's Logs tab and the Logs page: its colour codes as coloured runs, its
/// search matches marked, brushes cached and shared by every line, and a row recycled for another line drawn again from that line alone.
/// </summary>
internal static class LogText
{
    // Past this many cached colour brushes (a log drawing 24-bit gradients) the cache starts over.
    private const int MaxColorBrushes = 1024;

    private static readonly Dictionary<(string Key, bool Dark), Brush?> ThemeBrushes = [];

    /// <summary>One brush per colour a styled line uses, shared by every line.</summary>
    private static readonly Dictionary<Windows.UI.Color, SolidColorBrush> ColorBrushes = [];

    /// <summary>The look each row not yet in the window still has to apply when it loads; at most one per row, the latest render's.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, RoutedEventHandler> PendingMarks = new();

    /// <summary>The styled line each row shows in coloured runs, so a render that shows it again changes nothing.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, Painted> PaintedRows = new();

    private static FontFamily? _mono;

    /// <summary>Cascadia Mono, or Consolas where it is not installed; one instance for every line.</summary>
    public static FontFamily Mono => _mono ??= new FontFamily("Cascadia Mono, Consolas");

    /// <summary>
    /// Draws <paramref name="look"/> on <paramref name="block"/> and marks matches <paramref name="first"/> to <paramref name="first"/> +
    /// <paramref name="count"/>, in the colours of the block's own theme; a block not in the window yet is drawn in
    /// <paramref name="appDark"/>'s and again once it is.
    /// </summary>
    public static void Mark(TextBlock block, LineLook look, IReadOnlyList<LogMatch> matches, int first, int count, int offset, int current, bool appDark)
    {
        // Only this render's line and matches may mark the block: drop a mark still waiting from an earlier render.
        if (PendingMarks.TryGetValue(block, out var pending))
        {
            block.Loaded -= pending;
            PendingMarks.Remove(block);
        }

        if (!block.IsLoaded && (count > 0 || look.Styled))
        {
            void OnLoaded(object sender, RoutedEventArgs e) => Mark(block, look, matches, first, count, offset, current, appDark);

            block.Loaded += OnLoaded;
            PendingMarks.Add(block, OnLoaded);
        }

        var dark = block.IsLoaded ? block.ActualTheme == ElementTheme.Dark : appDark;
        var backgrounds = Paint(block, look, dark);

        block.TextHighlighters.Clear();
        foreach (var background in backgrounds)
            block.TextHighlighters.Add(background);
        if (count == 0)
            return;

        var others = new TextHighlighter
        {
            Background = ThemeBrush("SystemFillColorCautionBackgroundBrush", dark),
            Foreground = ThemeBrush("TextFillColorPrimaryBrush", dark),
        };
        var chosen = new TextHighlighter
        {
            Background = ThemeBrush("AccentFillColorDefaultBrush", dark),
            Foreground = ThemeBrush("TextOnAccentFillColorPrimaryBrush", dark),
        };

        for (var i = first; i < first + count; i++)
        {
            var range = new TextRange { StartIndex = offset + matches[i].Start, Length = matches[i].Length };
            (i == current ? chosen : others).Ranges.Add(range);
        }

        if (others.Ranges.Count > 0)
            block.TextHighlighters.Add(others);
        if (chosen.Ranges.Count > 0)
            block.TextHighlighters.Add(chosen);
    }

    /// <summary>
    /// Shows a styled line (<see cref="LineLook.Styled"/>) in runs: plain text as the block's own, each span in its colour, bold, dim
    /// (its colour at 60 %), italic or underlined, its background drawn by a highlighter under the search's marks. Returns those
    /// highlighters. Done once per line, timestamps and theme: a render that shows the same again reuses the runs and highlighters.
    /// A row that showed a styled line and now shows a plain one gets one plain run back (Reactor sets the text only when it changes).
    /// </summary>
    private static TextHighlighter[] Paint(TextBlock block, LineLook look, bool dark)
    {
        PaintedRows.TryGetValue(block, out var painted);
        if (!look.Styled)
        {
            if (painted is not null)
            {
                PaintedRows.Remove(block);
                block.Inlines.Clear();
                block.Inlines.Add(new Run { Text = LogView.LineText(look.Line, look.Timestamps) });
            }

            return [];
        }

        if (painted is not null && ReferenceEquals(painted.Line, look.Line) && painted.Timestamps == look.Timestamps && painted.Dark == dark)
            return painted.Backgrounds;

        var line = look.Line;
        var text = line.Text;
        var offset = LogView.TextOffset(line, look.Timestamps);
        var plainFore = ThemeColor(line.Stream == LogStream.Stderr ? "SystemFillColorCaution" : "TextFillColorPrimary", dark);
        Dictionary<(Windows.UI.Color Back, Windows.UI.Color Fore), TextHighlighter>? backgrounds = null;

        var inlines = block.Inlines;
        inlines.Clear();
        if (offset > 0)
            inlines.Add(new Run { Text = LogView.LineText(line, look.Timestamps)[..offset] });

        var at = 0;
        foreach (var span in line.Spans)
        {
            if (span.Start > at)
                inlines.Add(new Run { Text = text[at..span.Start] });

            var style = span.Style;
            var run = new Run { Text = text.Substring(span.Start, span.Length) };
            Windows.UI.Color? fore = LogColors.Rgb(style.Foreground, dark) is { } rgb ? Opaque(rgb) : null;
            if (style.Effects.HasFlag(TextEffects.Dim))
            {
                var color = fore ?? plainFore;
                fore = Windows.UI.Color.FromArgb((byte)(color.A * 0.6), color.R, color.G, color.B);
            }

            if (fore is { } f)
                run.Foreground = ColorBrush(f);
            if (style.Effects.HasFlag(TextEffects.Bold))
                run.FontWeight = FontWeights.Bold;
            if (style.Effects.HasFlag(TextEffects.Italic))
                run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (style.Effects.HasFlag(TextEffects.Underline))
                run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
            inlines.Add(run);

            if (LogColors.Rgb(style.Background, dark) is { } backRgb)
            {
                backgrounds ??= [];
                var key = (Back: Opaque(backRgb), Fore: fore ?? plainFore);
                if (!backgrounds.TryGetValue(key, out var highlighter))
                {
                    highlighter = new TextHighlighter { Background = ColorBrush(key.Back), Foreground = ColorBrush(key.Fore) };
                    backgrounds[key] = highlighter;
                }

                highlighter.Ranges.Add(new TextRange { StartIndex = offset + span.Start, Length = span.Length });
            }

            at = span.Start + span.Length;
        }

        if (at < text.Length)
            inlines.Add(new Run { Text = text[at..] });

        TextHighlighter[] result = backgrounds is null ? [] : [.. backgrounds.Values];
        PaintedRows.AddOrUpdate(block, new Painted(line, look.Timestamps, dark, result));
        return result;
    }

    public static Windows.UI.Color Opaque(int rgb) => Windows.UI.Color.FromArgb(0xff, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static SolidColorBrush ColorBrush(Windows.UI.Color color)
    {
        if (ColorBrushes.TryGetValue(color, out var brush))
            return brush;
        if (ColorBrushes.Count >= MaxColorBrushes)
            ColorBrushes.Clear();
        brush = new SolidColorBrush(color);
        ColorBrushes[color] = brush;
        return brush;
    }

    /// <summary>The theme colour <paramref name="key"/> of the light or dark theme, from <see cref="ThemeBrush"/>.</summary>
    private static Windows.UI.Color ThemeColor(string key, bool dark) =>
        ThemeBrush(key + "Brush", dark) is SolidColorBrush brush ? brush.Color : Windows.UI.Color.FromArgb(0xff, 0x80, 0x80, 0x80);

    /// <summary>
    /// The theme brush <paramref name="key"/> ("…Brush") of the light or dark theme. Built from the theme's colour of the same name
    /// without "Brush": a theme brush read in code takes its colour from the app's theme, not from the theme it was asked for.
    /// </summary>
    private static Brush? ThemeBrush(string key, bool dark)
    {
        if (ThemeBrushes.TryGetValue((key, dark), out var cached))
            return cached;

        var colorKey = key.EndsWith("Brush", StringComparison.Ordinal) ? key[..^"Brush".Length] : key;
        var theme = dark ? "Default" : "Light";
        Brush? brush = null;
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries.Prepend(Application.Current.Resources))
        {
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed)
                && themed is ResourceDictionary resources
                && resources.TryGetValue(colorKey, out var value)
                && value is Windows.UI.Color color)
            {
                brush = new SolidColorBrush(color);
                break;
            }
        }

        // Not found (a key with no colour of its name): the highlighter's own default colours apply.
        ThemeBrushes[(key, dark)] = brush;
        return brush;
    }

    /// <summary>A row's line and how it is drawn: with its timestamp or not, and in its colours (<see cref="Styled"/>) or plain.</summary>
    public sealed record LineLook(LogLine Line, bool Timestamps, bool Styled);

    /// <summary>What <see cref="Paint"/> drew on a row: the line, in which theme, and the highlighters of its backgrounds.</summary>
    private sealed record Painted(LogLine Line, bool Timestamps, bool Dark, TextHighlighter[] Backgrounds);
}

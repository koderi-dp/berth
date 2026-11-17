using System.Collections.Immutable;

namespace Berth.Engine.Models;

/// <summary>Which of a container's output streams a log line came from.</summary>
public enum LogStream
{
    Stdout,
    Stderr,
}

/// <summary>
/// One line of a container's log. <see cref="Text"/> has no trailing newline and no ANSI escape codes (the colours they set are
/// <see cref="Spans"/>);
/// <see cref="Timestamp"/> is null when the line did not start with one.
/// </summary>
public sealed record LogLine(LogStream Stream, DateTimeOffset? Timestamp, string Text)
{
    /// <summary>
    /// The styled parts of <see cref="Text"/> its colour codes asked for, in order and not overlapping; text outside them is plain.
    /// Empty (the one shared empty array) for a line without colour codes.
    /// </summary>
    public ImmutableArray<TextSpan> Spans { get; init; } = ImmutableArray<TextSpan>.Empty;
}

/// <summary>Which log lines to read:<see cref="Tail"/> lines from the end (-1 for all), then new ones as they come when <see cref="Follow"/>.</summary>
public sealed record LogRequest(int Tail, bool Follow)
{
    /// <summary>Only lines written at or after this time; null for no limit.</summary>
    public DateTimeOffset? Since { get; init; }
}

/// <summary><see cref="Length"/> characters of a text from <see cref="Start"/>, drawn in <see cref="Style"/>.</summary>
public readonly record struct TextSpan(int Start, int Length, TextStyle Style);

/// <summary>How text is drawn, as SGR codes (<c>ESC[…m</c>) set it.</summary>
public readonly record struct TextStyle(AnsiColor Foreground, AnsiColor Background, TextEffects Effects)
{
    /// <summary>The default colours and no effects: what a line starts with and <c>ESC[0m</c> returns to.</summary>
    public static readonly TextStyle Plain = new(AnsiColor.Default, AnsiColor.Default, TextEffects.None);

    public bool IsPlain => this == Plain;
}

/// <summary>The effects SGR codes turn on besides colour.</summary>
[Flags]
public enum TextEffects
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
}

/// <summary>
/// A colour as a terminal names it: the default (the text's or background's own), an index into the 256-colour palette (0-15 the
/// sixteen ANSI colours, 16-231 the 6x6x6 cube, 232-255 the greys) or a 24-bit RGB colour. Which RGB a palette index is depends on
/// the theme, so it is left to the view.
/// </summary>
public readonly record struct AnsiColor
{
    private const int RgbFlag = 0x1000000;

    // 0 is the default, so default(AnsiColor) is too; an index is stored plus one; an RGB colour with RgbFlag set.
    private readonly int _value;

    private AnsiColor(int value) => _value = value;

    public static AnsiColor Default => default;

    public bool IsDefault => _value == 0;

    /// <summary>Whether this is a 24-bit colour (<see cref="Rgb"/>) rather than a palette index (<see cref="Index"/>).</summary>
    public bool IsRgb => (_value & RgbFlag) != 0;

    /// <summary>The palette index, 0-255; meaningful when the colour is neither default nor RGB.</summary>
    public int Index => _value - 1;

    /// <summary>The colour as 0xRRGGBB; meaningful when <see cref="IsRgb"/>.</summary>
    public int Rgb => _value & 0xFFFFFF;

    public static AnsiColor FromIndex(int index) => new(Math.Clamp(index, 0, 255) + 1);

    public static AnsiColor FromRgb(int r, int g, int b) =>
        new(RgbFlag | (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255));
}

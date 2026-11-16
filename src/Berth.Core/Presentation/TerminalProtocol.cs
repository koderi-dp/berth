using System.Buffers;
using System.Text;
using System.Text.Json;
using Berth.Engine.Models;

namespace Berth.Presentation;

/// <summary>A message from the terminal page to the app.</summary>
public abstract record TerminalMessage
{
    /// <summary>xterm is open and fitted to <see cref="Size"/>. Sent once per page load.</summary>
    public sealed record Ready(TerminalSize Size) : TerminalMessage;

    /// <summary>Bytes typed or pasted, for the shell.</summary>
    public sealed record Input(ReadOnlyMemory<byte> Bytes) : TerminalMessage;

    /// <summary>The fitted size changed.</summary>
    public sealed record Resize(TerminalSize Size) : TerminalMessage;

    /// <summary>xterm finished writing <see cref="Bytes"/> bytes of output.</summary>
    public sealed record Ack(int Bytes) : TerminalMessage;

    /// <summary>Put <see cref="Text"/> on the clipboard.</summary>
    public sealed record Copy(string Text) : TerminalMessage;

    /// <summary>Send the clipboard's text.</summary>
    public sealed record Paste : TerminalMessage;

    /// <summary>F6: move the focus out of the terminal.</summary>
    public sealed record Leave : TerminalMessage;
}

/// <summary>
/// The messages between the app and the terminal page: one JSON object each, with a string <c>t</c>; bytes are standard base64 in
/// <c>d</c>. Written with <see cref="Utf8JsonWriter"/> and read with <see cref="Utf8JsonReader"/>: no reflection. Text is escaped
/// for any context (quotes, line breaks, <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and non-ASCII become escapes).
/// </summary>
public static class TerminalProtocol
{
    private static readonly string ResetMessage = Write(w => w.WriteString("t", "reset"));

    private static readonly string FocusMessage = Write(w => w.WriteString("t", "focus"));

    private static readonly string EnabledOn = Write(w => Enabled(w, true));

    private static readonly string EnabledOff = Write(w => Enabled(w, false));

    /// <summary>
    /// Reads a message from the page. Null for anything malformed, unknown or out of range: not one JSON object, an unknown or
    /// missing <c>t</c>, a missing or mistyped field, base64 that does not decode, a size outside 2 to 1000 columns and 1 to 500
    /// rows, or a negative count. Never throws.
    /// </summary>
    public static TerminalMessage? Parse(string json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return Read(Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Bytes from the shell, as they came: <c>{"t":"output","d":"&lt;base64&gt;"}</c>.</summary>
    public static string Output(ReadOnlySpan<byte> bytes)
    {
        var buffer = new ArrayBufferWriter<byte>(32 + ((bytes.Length + 2) / 3 * 4));
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("t", "output");
            writer.WriteBase64String("d", bytes);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The clipboard's text for the page to paste: <c>{"t":"paste","d":"text"}</c>.</summary>
    public static string PasteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Write(w =>
        {
            w.WriteString("t", "paste");
            w.WriteString("d", text);
        });
    }

    /// <summary>A new shell starts: clear the screen and the scrollback.</summary>
    public static string Reset() => ResetMessage;

    /// <summary>Whether typing is accepted (off once the shell ended).</summary>
    public static string Enabled(bool on) => on ? EnabledOn : EnabledOff;

    /// <summary>Take the keyboard focus.</summary>
    public static string Focus() => FocusMessage;

    /// <summary>The colours and the minimum contrast ratio.</summary>
    public static string Theme(TerminalColors colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        return Write(w =>
        {
            w.WriteString("t", "theme");
            w.WriteString("background", colors.Background);
            w.WriteString("foreground", colors.Foreground);
            w.WriteString("cursor", colors.Cursor);
            w.WriteString("selection", colors.Selection);
            w.WriteStartArray("ansi");
            foreach (var color in colors.Ansi)
                w.WriteStringValue(color);
            w.WriteEndArray();
            w.WriteNumber("contrast", colors.Contrast);
        });
    }

    private static void Enabled(Utf8JsonWriter writer, bool on)
    {
        writer.WriteString("t", "enabled");
        writer.WriteBoolean("on", on);
    }

    /// <summary>One object with the properties <paramref name="properties"/> writes.</summary>
    private static string Write(Action<Utf8JsonWriter> properties)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            properties(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The fields of one object, by name; a repeated name keeps its last value. Values other than objects and arrays only.</summary>
    private static TerminalMessage? Read(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return null;

        string? type = null;
        string? data = null;
        bool dataIsString = false;
        int? columns = null, rows = null, count = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            if (!reader.Read())
                return null;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
                continue;
            }

            switch (name)
            {
                case "t":
                    type = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    break;
                case "d":
                    dataIsString = reader.TokenType == JsonTokenType.String;
                    data = dataIsString ? reader.GetString() : null;
                    break;
                case "cols":
                    columns = Int(ref reader);
                    break;
                case "rows":
                    rows = Int(ref reader);
                    break;
                case "n":
                    count = Int(ref reader);
                    break;
            }
        }

        // The object must close, and nothing may follow it.
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            return null;

        return type switch
        {
            "ready" => Size(columns, rows) is { } size ? new TerminalMessage.Ready(size) : null,
            "resize" => Size(columns, rows) is { } size ? new TerminalMessage.Resize(size) : null,
            "input" when dataIsString => new TerminalMessage.Input(Convert.FromBase64String(data!)),
            "ack" when count is >= 0 => new TerminalMessage.Ack(count.Value),
            "copy" when dataIsString => new TerminalMessage.Copy(data!),
            "paste" => new TerminalMessage.Paste(),
            "leave" => new TerminalMessage.Leave(),
            _ => null,
        };
    }

    private static int? Int(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) ? value : null;

    private static TerminalSize? Size(int? columns, int? rows) =>
        columns is { } c && rows is { } r && new TerminalSize(c, r) is { IsValid: true } size ? size : null;
}

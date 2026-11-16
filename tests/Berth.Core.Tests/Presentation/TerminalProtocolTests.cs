using System.Collections.Immutable;
using System.Text.Json;
using Berth.Engine.Models;
using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class TerminalProtocolTests
{
    // Page to host.

    [Fact]
    public void Ready_carries_the_fitted_size() =>
        Assert.Equal(
            new TerminalMessage.Ready(new TerminalSize(80, 24)),
            TerminalProtocol.Parse("""{"t":"ready","cols":80,"rows":24}"""));

    [Fact]
    public void Input_carries_bytes()
    {
        var message = Assert.IsType<TerminalMessage.Input>(TerminalProtocol.Parse("""{"t":"input","d":"AwFo/w=="}"""));

        Assert.Equal([0x03, 0x01, 0x68, 0xFF], message.Bytes.ToArray());
    }

    [Fact]
    public void Resize_carries_the_new_size() =>
        Assert.Equal(
            new TerminalMessage.Resize(new TerminalSize(120, 30)),
            TerminalProtocol.Parse("""{"t":"resize","cols":120,"rows":30}"""));

    [Fact]
    public void Ack_carries_a_byte_count() =>
        Assert.Equal(new TerminalMessage.Ack(4096), TerminalProtocol.Parse("""{"t":"ack","n":4096}"""));

    [Fact]
    public void Copy_carries_plain_text() =>
        Assert.Equal(new TerminalMessage.Copy("ls -la\n\"x\""), TerminalProtocol.Parse("""{"t":"copy","d":"ls -la\n\"x\""}"""));

    [Fact]
    public void Paste_asks_for_the_clipboard() =>
        Assert.Equal(new TerminalMessage.Paste(), TerminalProtocol.Parse("""{"t":"paste"}"""));

    [Fact]
    public void Leave_asks_to_move_the_focus_out() =>
        Assert.Equal(new TerminalMessage.Leave(), TerminalProtocol.Parse("""{"t":"leave"}"""));

    // Host to page.

    [Fact]
    public void Output_is_base64() =>
        Assert.Equal("""{"t":"output","d":"AP8bWw=="}""", TerminalProtocol.Output([0x00, 0xFF, 0x1B, 0x5B]));

    [Fact]
    public void Paste_text_is_plain_text() =>
        Assert.Equal("""{"t":"paste","d":"echo hi"}""", TerminalProtocol.PasteText("echo hi"));

    [Fact]
    public void Reset_clears_the_terminal() =>
        Assert.Equal("""{"t":"reset"}""", TerminalProtocol.Reset());

    [Fact]
    public void Enabled_says_whether_typing_is_taken()
    {
        Assert.Equal("""{"t":"enabled","on":true}""", TerminalProtocol.Enabled(true));
        Assert.Equal("""{"t":"enabled","on":false}""", TerminalProtocol.Enabled(false));
    }

    [Fact]
    public void Focus_asks_the_page_to_take_the_keyboard() =>
        Assert.Equal("""{"t":"focus"}""", TerminalProtocol.Focus());

    [Fact]
    public void Theme_carries_every_colour_and_the_contrast()
    {
        ImmutableArray<string> ansi = [.. Enumerable.Range(0, 16).Select(i => $"#0000{i:x2}")];
        var colors = new TerminalColors("#202020", "#ffffff", "#ffffff", "#636363", ansi, 4.5);

        var json = TerminalProtocol.Theme(colors);

        Assert.Equal(
            """{"t":"theme","background":"#202020","foreground":"#ffffff","cursor":"#ffffff","selection":"#636363","ansi":[""" +
            string.Join(",", ansi.Select(a => $"\"{a}\"")) + """],"contrast":4.5}""",
            json);
        Assert.EndsWith("\"contrast\":7}", TerminalProtocol.Theme(colors with { Contrast = 7 }), StringComparison.Ordinal);
    }

    [Fact]
    public void Any_bytes_round_trip_through_output_and_input()
    {
        byte[][] cases =
        [
            [.. Enumerable.Range(0, 256).Select(i => (byte)i)],
            [0xC3, 0x28, 0xE2, 0x82, 0xF0, 0x9F, 0x98, 0xED, 0xA0, 0x80],
            [],
        ];

        foreach (var bytes in cases)
        {
            using var output = JsonDocument.Parse(TerminalProtocol.Output(bytes));
            var d = output.RootElement.GetProperty("d").GetString()!;
            Assert.Equal(bytes, Convert.FromBase64String(d));

            var input = Assert.IsType<TerminalMessage.Input>(TerminalProtocol.Parse($$"""{"t":"input","d":"{{d}}"}"""));
            Assert.Equal(bytes, input.Bytes.ToArray());
        }
    }

    [Fact]
    public void Malformed_unknown_and_out_of_range_messages_are_null()
    {
        string[] bad =
        [
            "", "{", "null", "[]", "42", "\"ready\"", "{}", """{"t":1}""", """{"t":"bell"}""", """{"type":"leave"}""",
            """{"t":"input"}""", """{"t":"input","d":"not base64!"}""", """{"t":"input","d":3}""",
            """{"t":"ready","cols":1,"rows":24}""", """{"t":"ready","cols":1001,"rows":24}""",
            """{"t":"ready","cols":80,"rows":0}""", """{"t":"resize","cols":80,"rows":501}""", """{"t":"resize","cols":80}""",
            """{"t":"resize","cols":80.5,"rows":24}""", """{"t":"resize","cols":"80","rows":24}""",
            """{"t":"resize","cols":99999999999,"rows":24}""",
            """{"t":"ack","n":-1}""", """{"t":"ack"}""", """{"t":"ack","n":1.5}""", """{"t":"copy"}""", """{"t":"copy","d":null}""",
            """{"t":"leave"} trailing""",
        ];

        foreach (var json in bad)
            Assert.True(TerminalProtocol.Parse(json) is null, json);
    }

    [Fact]
    public void Text_with_quotes_newlines_and_script_tags_is_escaped_in_paste()
    {
        const string Text = "say \"hi\"\r\nit's </script><script>alert(1)</script> & \\ \u2028 é";

        var json = TerminalProtocol.PasteText(Text);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("paste", parsed.RootElement.GetProperty("t").GetString());
        Assert.Equal(Text, parsed.RootElement.GetProperty("d").GetString());
        Assert.DoesNotContain("<", json, StringComparison.Ordinal);
        Assert.DoesNotContain(">", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2028", json, StringComparison.Ordinal);
    }
}

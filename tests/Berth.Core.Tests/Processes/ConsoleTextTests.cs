using System.Text;
using Berth.Processes;

namespace Berth.Tests.Processes;

public sealed class ConsoleTextTests
{
    [Fact]
    public void Utf16_output_as_wsl_writes_it_is_decoded()
    {
        Assert.Equal("Ubuntu-24.04\r\n", ConsoleText.Decode(Encoding.Unicode.GetBytes("Ubuntu-24.04\r\n")));
    }

    [Fact]
    public void Utf16_with_a_byte_order_mark_is_decoded_without_it()
    {
        Assert.Equal("Ubuntu", ConsoleText.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Ubuntu")]));
    }

    [Fact]
    public void Utf8_output_is_decoded()
    {
        Assert.Equal("Failed to stop docker.service: Access denied ✓", ConsoleText.Decode(Encoding.UTF8.GetBytes("Failed to stop docker.service: Access denied ✓")));
        Assert.Equal("a", ConsoleText.Decode([0xEF, 0xBB, 0xBF, (byte)'a']));
    }

    [Fact]
    public void Nothing_is_empty()
    {
        Assert.Equal("", ConsoleText.Decode([]));
    }

    [Fact]
    public void The_last_lines_are_kept_and_trailing_blank_lines_dropped()
    {
        var text = string.Join("\r\n", Enumerable.Range(1, 30).Select(i => "line " + i)) + "\r\n\r\n";

        var tail = ConsoleText.LastLines(text, 20);

        Assert.Equal(string.Join("\n", Enumerable.Range(11, 20).Select(i => "line " + i)), tail);
    }
}

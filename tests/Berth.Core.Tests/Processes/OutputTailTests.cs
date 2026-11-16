using System.Text;
using Berth.Processes;

namespace Berth.Tests.Processes;

public sealed class OutputTailTests
{
    [Fact]
    public void Keeps_everything_under_the_bound()
    {
        var tail = new OutputTail(16);
        tail.Append(Encoding.UTF8.GetBytes("hello "));
        tail.Append(Encoding.UTF8.GetBytes("world"));

        Assert.Equal("hello world", Encoding.UTF8.GetString(tail.ToArray()));
    }

    [Fact]
    public void Keeps_only_the_last_bytes_over_the_bound()
    {
        var tail = new OutputTail(8);
        tail.Append(Encoding.UTF8.GetBytes("0123456789"));
        tail.Append(Encoding.UTF8.GetBytes("ab"));

        Assert.Equal("456789ab", Encoding.UTF8.GetString(tail.ToArray()));
    }

    [Fact]
    public void Drops_a_whole_byte_pair_so_utf16_stays_aligned()
    {
        var tail = new OutputTail(7);
        tail.Append(Encoding.Unicode.GetBytes("abcdef"));

        Assert.Equal("def", ConsoleText.Decode(tail.ToArray()));
        Assert.Equal(6, tail.ToArray().Length);
    }
}

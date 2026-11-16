using Berth.State;
using static Berth.Tests.Presentation.DetailsFixtures;

namespace Berth.Tests.State;

public sealed class LogBufferTests
{
    [Fact]
    public void Empty_buffer_has_no_lines_and_starts_numbering_at_one()
    {
        Assert.Empty(LogBuffer.Empty.Lines);
        Assert.Equal(0, LogBuffer.Empty.Dropped);
        Assert.Equal(1, LogBuffer.Empty.FirstNumber);
    }

    [Fact]
    public void Append_adds_lines_in_order_and_leaves_the_original_unchanged()
    {
        var first = LogBuffer.Empty.Append(Lines(1, 2));
        var second = first.Append(Lines(3, 1));

        Assert.Equal(["line 1", "line 2"], first.Lines.Select(l => l.Text));
        Assert.Equal(["line 1", "line 2", "line 3"], second.Lines.Select(l => l.Text));
        Assert.Equal(1, second.FirstNumber);
        Assert.Equal(0, second.Dropped);
    }

    [Fact]
    public void Append_of_nothing_returns_the_same_buffer()
    {
        var buffer = LogBuffer.Empty.Append(Lines(1, 2));

        Assert.Same(buffer, buffer.Append([]));
    }

    [Fact]
    public void Append_past_capacity_drops_the_oldest_lines_and_counts_them()
    {
        var buffer = LogBuffer.Empty.Append(Lines(1, LogBuffer.Capacity)).Append(Lines(LogBuffer.Capacity + 1, 5));

        Assert.Equal(LogBuffer.Capacity, buffer.Lines.Count);
        Assert.Equal(5, buffer.Dropped);
        Assert.Equal(6, buffer.FirstNumber);
        Assert.Equal("line 6", buffer.Lines[0].Text);
        Assert.Equal($"line {LogBuffer.Capacity + 5}", buffer.Lines[^1].Text);
    }

    [Fact]
    public void One_append_larger_than_capacity_keeps_only_its_newest_lines()
    {
        var buffer = LogBuffer.Empty.Append(Lines(1, 3)).Append(Lines(4, LogBuffer.Capacity + 2));

        Assert.Equal(LogBuffer.Capacity, buffer.Lines.Count);
        Assert.Equal(5, buffer.Dropped);
        Assert.Equal(6, buffer.FirstNumber);
        Assert.Equal("line 6", buffer.Lines[0].Text);
    }

    [Fact]
    public void Clear_empties_the_view_and_numbering_continues()
    {
        var cleared = LogBuffer.Empty.Append(Lines(1, 3)).Clear();
        var after = cleared.Append(Lines(4, 2));

        Assert.Empty(cleared.Lines);
        Assert.Equal(4, cleared.FirstNumber);
        Assert.Equal(["line 4", "line 5"], after.Lines.Select(l => l.Text));
        Assert.Equal(4, after.FirstNumber);
    }

    [Fact]
    public void Clear_after_lines_were_dropped_resets_the_dropped_count_and_keeps_numbering()
    {
        var full = LogBuffer.Empty.Append(Lines(1, LogBuffer.Capacity + 2));
        var after = full.Clear().Append(Lines(LogBuffer.Capacity + 3, 1));

        Assert.Equal(2, full.Dropped);
        Assert.Equal(0, after.Dropped);
        Assert.Equal(LogBuffer.Capacity + 3, after.FirstNumber);
    }
}

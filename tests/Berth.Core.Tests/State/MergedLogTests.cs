using Berth.Engine.Models;
using Berth.State;

namespace Berth.Tests.State;

public sealed class MergedLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly LogOrigin A = new("a", "a", null, null);

    private static LogEntry Entry(long sequence, DateTimeOffset time) => new(sequence, A, new LogLine(LogStream.Stdout, time, $"#{sequence}"), time);

    private static IEnumerable<long> Sequences(MergedLog log) => log.Entries.Select(e => e.Sequence);

    [Fact]
    public void Newer_lines_go_at_the_end()
    {
        var log = MergedLog.Empty.Add([Entry(1, T0), Entry(2, T0.AddSeconds(1)), Entry(3, T0.AddSeconds(2))]);

        Assert.Equal([1L, 2L, 3L], Sequences(log));
    }

    [Fact]
    public void An_older_line_is_placed_by_its_second()
    {
        var log = MergedLog.Empty.Add([Entry(1, T0), Entry(2, T0.AddSeconds(5))]).Add([Entry(3, T0.AddSeconds(2))]);

        Assert.Equal([1L, 3L, 2L], Sequences(log));
    }

    [Fact]
    public void Lines_within_one_second_keep_their_arrival_order_whatever_their_fraction()
    {
        var log = MergedLog.Empty.Add([Entry(1, T0.AddMilliseconds(900))]).Add([Entry(2, T0.AddMilliseconds(100))]);

        Assert.Equal([1L, 2L], Sequences(log));
    }

    [Fact]
    public void Past_the_cap_the_oldest_lines_go_and_are_counted()
    {
        var entries = Enumerable.Range(1, MergedLog.Capacity + 5).Select(i => Entry(i, T0.AddSeconds(i))).ToArray();

        var log = MergedLog.Empty.Add(entries);

        Assert.Equal(MergedLog.Capacity, log.Entries.Count);
        Assert.Equal(5, log.Dropped);
        Assert.Equal(6, log.Entries[0].Sequence);
    }

    [Fact]
    public void A_line_older_than_everything_in_a_full_log_is_dropped_at_once()
    {
        var full = MergedLog.Empty.Add([.. Enumerable.Range(1, MergedLog.Capacity).Select(i => Entry(i, T0.AddSeconds(i)))]);

        var log = full.Add([Entry(MergedLog.Capacity + 1, T0.AddSeconds(-10))]);

        Assert.Equal(MergedLog.Capacity, log.Entries.Count);
        Assert.Equal(1, log.Dropped);
        Assert.Equal(1, log.Entries[0].Sequence);
    }

    [Fact]
    public void Adding_nothing_returns_the_same_log()
    {
        var log = MergedLog.Empty.Add([Entry(1, T0)]);

        Assert.Same(log, log.Add([]));
    }

    [Fact]
    public void A_compose_service_is_labelled_project_slash_service_and_others_by_name()
    {
        Assert.Equal("shop/web", new LogOrigin("x", "shop-web-1", "shop", "web").Label);
        Assert.Equal("shop-web-1", new LogOrigin("x", "shop-web-1", "shop", null).Label);
        Assert.Equal("db", new LogOrigin("x", "db", null, null).Label);
    }
}

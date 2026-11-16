using Berth.State;
using static Berth.Tests.Presentation.DetailsFixtures;

namespace Berth.Tests.State;

public sealed class StatsHistoryTests
{
    [Fact]
    public void Empty_history_has_no_latest_sample_and_no_points()
    {
        var history = StatsHistory.Empty;

        Assert.Null(history.Latest);
        Assert.Empty(history.Cpu);
        Assert.Empty(history.Memory);
        Assert.Empty(history.NetworkReceived);
        Assert.Empty(history.NetworkSent);
        Assert.Empty(history.BlockRead);
        Assert.Empty(history.BlockWritten);
    }

    [Fact]
    public void Cpu_and_memory_are_taken_from_each_sample()
    {
        var latest = Sample(1, cpu: 12.5, memory: 2048);
        var history = StatsHistory.Empty.Add(Sample(0, cpu: 1.5, memory: 1024)).Add(latest);

        Assert.Same(latest, history.Latest);
        Assert.Equal([new StatsPoint(T0, 1.5), new StatsPoint(T0.AddSeconds(1), 12.5)], history.Cpu);
        Assert.Equal([new StatsPoint(T0, 1024), new StatsPoint(T0.AddSeconds(1), 2048)], history.Memory);
    }

    [Fact]
    public void Add_leaves_the_original_unchanged()
    {
        var first = StatsHistory.Empty.Add(Sample(0, cpu: 1));
        _ = first.Add(Sample(1, cpu: 2));

        Assert.Single(first.Cpu);
    }

    [Fact]
    public void Rates_are_the_change_per_second_between_consecutive_samples()
    {
        var history = StatsHistory.Empty
            .Add(Sample(0, received: 1000, sent: 100, read: 4096, written: 0))
            .Add(Sample(2, received: 3000, sent: 700, read: 8192, written: 1024));

        var later = T0.AddSeconds(2);
        Assert.Equal([new StatsPoint(T0, 0), new StatsPoint(later, 1000)], history.NetworkReceived);
        Assert.Equal([new StatsPoint(T0, 0), new StatsPoint(later, 300)], history.NetworkSent);
        Assert.Equal([new StatsPoint(T0, 0), new StatsPoint(later, 2048)], history.BlockRead);
        Assert.Equal([new StatsPoint(T0, 0), new StatsPoint(later, 512)], history.BlockWritten);
    }

    [Fact]
    public void Rate_is_zero_when_a_counter_went_down_after_a_restart()
    {
        var history = StatsHistory.Empty
            .Add(Sample(0, received: 5000, sent: 5000))
            .Add(Sample(1, received: 200, sent: 6000));

        Assert.Equal(0, history.NetworkReceived[^1].Value);
        Assert.Equal(1000, history.NetworkSent[^1].Value);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(4)]
    public void Sample_not_later_than_the_latest_is_ignored(double seconds)
    {
        var latest = Sample(5, cpu: 1, received: 1000);
        var history = StatsHistory.Empty.Add(Sample(0, cpu: 0)).Add(latest);

        var after = history.Add(Sample(seconds, cpu: 9, received: 9000));

        Assert.Same(history, after);
        Assert.Same(latest, after.Latest);
        Assert.Equal([T0, T0.AddSeconds(5)], after.Cpu.Select(p => p.Time));
    }

    [Fact]
    public void History_keeps_the_last_capacity_samples()
    {
        var history = StatsHistory.Empty;
        for (var i = 0; i < StatsHistory.Capacity + 5; i++)
        {
            history = history.Add(Sample(i, cpu: i, received: i * 10L));
        }

        Assert.Equal(StatsHistory.Capacity, history.Cpu.Count);
        Assert.Equal(StatsHistory.Capacity, history.Memory.Count);
        Assert.Equal(StatsHistory.Capacity, history.NetworkReceived.Count);
        Assert.Equal(StatsHistory.Capacity, history.BlockWritten.Count);
        Assert.Equal(new StatsPoint(T0.AddSeconds(5), 5), history.Cpu[0]);
        Assert.Equal(new StatsPoint(T0.AddSeconds(5), 10), history.NetworkReceived[0]);
        Assert.Equal(StatsHistory.Capacity + 4, history.Latest!.CpuPercent);
    }
}

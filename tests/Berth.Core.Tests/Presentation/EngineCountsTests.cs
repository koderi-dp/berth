using Berth.Engine.Models;
using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class EngineCountsTests
{
    [Fact]
    public void Paused_containers_are_counted_between_running_and_stopped() =>
        Assert.Equal("4 running · 1 paused · 2 stopped", EngineCounts.Text(Info(running: 4, paused: 1, stopped: 2)));

    [Fact]
    public void No_paused_containers_leaves_the_paused_part_out() =>
        Assert.Equal("4 running · 2 stopped", EngineCounts.Text(Info(running: 4, paused: 0, stopped: 2)));

    [Fact]
    public void Zero_running_and_stopped_are_still_shown() =>
        Assert.Equal("0 running · 3 paused · 0 stopped", EngineCounts.Text(Info(running: 0, paused: 3, stopped: 0)));

    private static EngineInfo Info(int running, int paused, int stopped) =>
        new("host", "29.0.0", 4, 1024, running, paused, stopped, 10);
}

using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using static Berth.Tests.Presentation.DetailsFixtures;

namespace Berth.Tests.Presentation;

public sealed class StatsChartsTests
{
    private const long KB = 1024;
    private const long MB = 1024 * KB;
    private const long GB = 1024 * MB;

    private static IReadOnlyList<StatsPoint> Points(params double[] values) =>
        [.. values.Select((v, i) => new StatsPoint(T0.AddSeconds(i), v))];

    private static StatsSample Memory(long used, long limit) => new(T0, 0, used, limit, 0, 0, 0, 0, 1);

    [Theory]
    [InlineData(StreamStatus.Idle, false, StatsView.NotRunning)]
    [InlineData(StreamStatus.Ended, true, StatsView.NotRunning)]
    [InlineData(StreamStatus.Ended, false, StatsView.NotRunning)]
    [InlineData(StreamStatus.Loading, false, StatsView.Loading)]
    [InlineData(StreamStatus.Live, false, StatsView.Loading)]
    [InlineData(StreamStatus.Live, true, StatsView.Charts)]
    [InlineData(StreamStatus.Loading, true, StatsView.Charts)]
    [InlineData(StreamStatus.Failed, false, StatsView.Failed)]
    [InlineData(StreamStatus.Failed, true, StatsView.Failed)]
    public void The_tab_shows_charts_only_while_samples_come(StreamStatus status, bool hasSample, StatsView view) =>
        Assert.Equal(view, StatsCharts.View(status, hasSample, waiting: false));

    [Theory]
    [InlineData(StreamStatus.Idle, false, StatsView.Waiting)]
    [InlineData(StreamStatus.Loading, false, StatsView.Waiting)]
    [InlineData(StreamStatus.Live, false, StatsView.Waiting)]
    [InlineData(StreamStatus.Ended, false, StatsView.Waiting)]
    [InlineData(StreamStatus.Failed, false, StatsView.Waiting)]
    [InlineData(StreamStatus.Live, true, StatsView.Charts)]
    [InlineData(StreamStatus.Ended, true, StatsView.Charts)]
    [InlineData(StreamStatus.Failed, true, StatsView.Charts)]
    public void While_waiting_for_the_engine_the_tab_keeps_its_charts_or_says_it_waits(StreamStatus status, bool hasSample, StatsView view) =>
        Assert.Equal(view, StatsCharts.View(status, hasSample, waiting: true));

    [Fact]
    public void The_state_texts_say_what_is_going_on()
    {
        Assert.Equal("Getting container stats…", StatsCharts.LoadingText);
        Assert.Equal("Start the container to see its stats.", StatsCharts.NotRunningText);
        Assert.Equal("Waiting for the engine…", StatsCharts.WaitingText);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-30, 30)]
    [InlineData(-120, 120)]
    [InlineData(-200, 120)]
    [InlineData(5, 0)]
    public void A_point_sits_at_its_seconds_before_the_latest_sample_within_the_window(double offset, double expected) =>
        Assert.Equal(expected, StatsCharts.SecondsAgo(T0, T0.AddSeconds(offset)));

    [Theory]
    [InlineData(0, "now")]
    [InlineData(20, "20 s")]
    [InlineData(120, "120 s")]
    public void Time_ticks_read_in_seconds_ago(double secondsAgo, string label) =>
        Assert.Equal(label, StatsCharts.TimeTick(secondsAgo));

    [Theory]
    [InlineData(new double[0], null, 100)]
    [InlineData(new[] { 3.0, 12.0 }, null, 100)]
    [InlineData(new[] { 3.0, 230.0 }, null, 250)]
    [InlineData(new[] { 12.0 }, 0.5, 50)]
    [InlineData(new[] { 49.0 }, 0.5, 50)]
    [InlineData(new[] { 70.0 }, 0.5, 70)]
    [InlineData(new[] { 12.0 }, 2.0, 200)]
    [InlineData(new[] { 412.0 }, null, 500)]
    public void The_cpu_axis_reaches_the_limit_or_one_cpu_and_the_highest_value(double[] values, double? cpuLimit, double max)
    {
        var scale = StatsCharts.Cpu(Points(values), cpuLimit);

        Assert.Equal(new ChartScale(max, 1, "%"), scale);
    }

    [Theory]
    [InlineData(50, "50%")]
    [InlineData(12.5, "12.5%")]
    [InlineData(0, "0%")]
    public void Cpu_ticks_are_percentages(double units, string label) =>
        Assert.Equal(label, StatsCharts.Cpu([], null).TickLabel(units));

    [Fact]
    public void The_memory_limit_is_drawn_only_when_it_is_below_the_machines_memory()
    {
        Assert.Equal(2 * GB, StatsCharts.MemoryLimit(Memory(300 * MB, 2 * GB), 8 * GB));
        Assert.Null(StatsCharts.MemoryLimit(Memory(300 * MB, 8 * GB), 8 * GB));
        Assert.Null(StatsCharts.MemoryLimit(Memory(300 * MB, 0), 8 * GB));
        Assert.Null(StatsCharts.MemoryLimit(Memory(300 * MB, 2 * GB), 0));
        Assert.Null(StatsCharts.MemoryLimit(null, 8 * GB));
    }

    [Fact]
    public void The_memory_axis_is_in_the_unit_of_its_highest_value_and_ends_on_a_round_number()
    {
        Assert.Equal(new ChartScale(300, MB, "MB"), StatsCharts.Memory(Points(200 * MB, 281 * MB), limit: null));
        Assert.Equal(new ChartScale(1, MB, "MB"), StatsCharts.Memory(Points(990 * KB), limit: null));
        Assert.Equal(new ChartScale(1, MB, "MB"), StatsCharts.Memory([], limit: null));
    }

    [Fact]
    public void The_memory_axis_reaches_the_limit_when_one_is_drawn()
    {
        var scale = StatsCharts.Memory(Points(281 * MB), limit: (long)(1.87 * GB));

        Assert.Equal(new ChartScale(2, GB, "GB"), scale);
        Assert.Equal("1.5 GB", scale.TickLabel(1.5));
        Assert.Equal("0 GB", scale.TickLabel(0));
    }

    [Fact]
    public void A_rate_axis_covers_both_series_and_starts_at_one_kilobyte_a_second()
    {
        Assert.Equal(new ChartScale(2, KB, "KB/s"), StatsCharts.Rate(Points(1500, 10), Points(300)));
        Assert.Equal(new ChartScale(2, KB, "KB/s"), StatsCharts.Rate(Points(300), Points(1500, 10)));
        Assert.Equal(new ChartScale(1, KB, "KB/s"), StatsCharts.Rate(Points(200), Points(0)));
        Assert.Equal(new ChartScale(1, KB, "KB/s"), StatsCharts.Rate([], []));
        Assert.Equal(new ChartScale(2.5, MB, "MB/s"), StatsCharts.Rate(Points(2.5 * MB), []));
        Assert.Equal("0.4 KB/s", StatsCharts.Rate(Points(1500), []).TickLabel(0.4));
    }

    [Fact]
    public void A_scale_turns_a_value_into_its_units() =>
        Assert.Equal(1.5, new ChartScale(2, GB, "GB").ToUnits(1.5 * GB));

    [Fact]
    public void The_titles_show_the_latest_figures()
    {
        Assert.Equal("12.4%", StatsCharts.CpuValue(Sample(0, cpu: 12.44)));
        Assert.Equal("0.0%", StatsCharts.CpuValue(Sample(0, cpu: 0)));
        Assert.Equal("281 MB / 1.87 GB", StatsCharts.MemoryValue(Memory(281 * MB, (long)(1.87 * GB))));
        Assert.Equal("281 MB", StatsCharts.MemoryValue(Memory(281 * MB, 0)));
    }

    [Fact]
    public void A_screen_reader_hears_the_latest_and_highest_figures()
    {
        var history = StatsHistory.Empty
            .Add(Sample(0, cpu: 30, memory: 300 * MB, received: 0, sent: 0, read: 0, written: 0) with { MemoryLimitBytes = GB })
            .Add(Sample(1, cpu: 12.44, memory: 200 * MB, received: 2048, sent: 1024, read: 4096, written: 0) with { MemoryLimitBytes = GB })
            .Add(Sample(2, cpu: 5, memory: 100 * MB, received: 3584, sent: 1324, read: 4096, written: 512) with { MemoryLimitBytes = GB });

        Assert.Equal("CPU usage 5.0%. Highest in the last 2 minutes: 30.0%.", StatsCharts.CpuSummary(history));
        Assert.Equal("Memory usage 100 MB of 1 GB. Highest in the last 2 minutes: 300 MB.", StatsCharts.MemorySummary(history));
        Assert.Equal(
            "Network I/O: received 1.5 KB/s, sent 300 B/s. Highest in the last 2 minutes: received 2 KB/s, sent 1 KB/s.",
            StatsCharts.NetworkSummary(history));
        Assert.Equal(
            "Disk read/write: read 0 B/s, written 512 B/s. Highest in the last 2 minutes: read 4 KB/s, written 512 B/s.",
            StatsCharts.DiskSummary(history));
    }

    [Fact]
    public void Before_any_sample_a_screen_reader_hears_that_there_are_no_figures_yet() =>
        Assert.Equal("CPU usage: no figures yet.", StatsCharts.CpuSummary(StatsHistory.Empty));

    /// <summary>The step d3 picks for an axis from 0 to <paramref name="max"/> with <see cref="StatsCharts.TickCount"/> ticks.</summary>
    private static double D3Step(double max)
    {
        var raw = max / StatsCharts.TickCount;
        var power = Math.Floor(Math.Log10(raw));
        var error = raw / Math.Pow(10, power);
        var factor = error >= Math.Sqrt(50) ? 10 : error >= Math.Sqrt(10) ? 5 : error >= Math.Sqrt(2) ? 2 : 1;
        return factor * Math.Pow(10, power);
    }

    [Theory]
    [InlineData(1500)]
    [InlineData(1.465 * KB)]
    [InlineData(3.3 * MB)]
    [InlineData(123_456)]
    [InlineData(777 * KB)]
    [InlineData(5.1 * GB)]
    public void A_rate_axis_ends_on_a_tick_of_the_step_drawn_for_it(double bytesPerSecond)
    {
        var scale = StatsCharts.Rate(Points(bytesPerSecond), []);
        var steps = scale.Max / D3Step(scale.Max);

        Assert.Equal(Math.Round(steps), steps, 6);
    }

    [Theory]
    [InlineData(230.0)]
    [InlineData(73.0)]
    [InlineData(412.0)]
    [InlineData(1234.0)]
    public void The_cpu_axis_ends_on_a_tick_of_the_step_drawn_for_it(double highest)
    {
        var scale = StatsCharts.Cpu(Points(highest), null);
        var steps = scale.Max / D3Step(scale.Max);

        Assert.Equal(Math.Round(steps), steps, 6);
    }

    [Fact]
    public void Every_tick_of_an_axis_is_in_the_unit_of_its_maximum()
    {
        var scale = StatsCharts.Rate(Points(900 * KB), []);

        Assert.Equal(new ChartScale(1, MB, "MB/s"), scale);
        Assert.Equal(["0 MB/s", "0.2 MB/s", "0.4 MB/s", "0.6 MB/s", "0.8 MB/s", "1 MB/s"], new[] { 0, 0.2, 0.4, 0.6, 0.8, 1 }.Select(scale.TickLabel));
    }

    [Fact]
    public void A_rate_moving_around_a_unit_boundary_keeps_its_axis_while_its_highest_value_is_in_the_window()
    {
        // A rate that peaks at 1010 KB/s once, then wanders between 950 and 999 KB/s: each second's window still holds the peak.
        var values = new List<double> { 1010 * KB };
        var random = new Random(7);
        var first = StatsCharts.Rate(Points([.. values]), []);
        for (var i = 0; i < 60; i++)
        {
            values.Add((950 + random.Next(50)) * KB);
            Assert.Equal(first, StatsCharts.Rate(Points([.. values]), []));
        }
    }

    [Fact]
    public void A_higher_highest_value_never_gives_a_lower_axis()
    {
        double previous = 0;
        for (double bytes = 1; bytes < 8.0 * GB; bytes *= 1.07)
        {
            var scale = StatsCharts.Rate(Points(bytes), []);
            var top = scale.Max * scale.UnitSize;
            Assert.True(top >= previous, $"{bytes} B/s gave {scale.Max} {scale.Unit}, below the axis of a lower value");
            Assert.True(top >= bytes);
            Assert.InRange(scale.Max, 0, 999.999);
            previous = top;
        }
    }
}

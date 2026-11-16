using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;

namespace Berth.Tests.Presentation;

public sealed class StatsSummaryTests
{
    private static readonly EngineInfo Info = new("host", "29.0.0", 8, 16L << 30, 2, 0, 1, 3);

    private static ContainerSummary Container(string id, string state = "running") => new(
        id, [$"/{id}"], "nginx:latest", "sha256:1", "nginx", 1_700_000_000, state, "Up 1 minute", [],
        new Dictionary<string, string>());

    private static StatsSample Sample(double cpu, long memory) =>
        new(new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero), cpu, memory, 4096, 0, 0, 0, 0, 1);

    [Fact]
    public void Cpu_and_memory_are_summed_over_the_running_containers()
    {
        var state = EngineState.Initial with
        {
            Info = Info,
            Containers = [Container("a"), Container("b"), Container("c", state: "exited")],
            Stats = ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                [
                    KeyValuePair.Create("a", Sample(1.25, 1000)),
                    KeyValuePair.Create("b", Sample(0.5, 2000)),
                    KeyValuePair.Create("c", Sample(9, 9000)),
                ]),
        };

        var totals = StatsSummary.Totals(state);

        Assert.Equal(new StatsTotals(1.75, 8, 3000, 16L << 30), totals);
    }

    [Fact]
    public void Without_stats_or_engine_info_everything_is_zero() =>
        Assert.Equal(new StatsTotals(0, 0, 0, 0), StatsSummary.Totals(EngineState.Initial));

    [Theory]
    [InlineData(1.4, 8, "1.4% / 800% (8 CPUs)")]
    [InlineData(12.44, 8, "12.4% / 800% (8 CPUs)")]
    [InlineData(0, 1, "0.0% / 100% (1 CPU)")]
    [InlineData(1.4, 0, "1.4%")]
    public void The_cpu_figure_is_out_of_every_cpu_of_the_engine(double cpu, int cpus, string text) =>
        Assert.Equal(text, StatsSummary.CpuText(new StatsTotals(cpu, cpus, 0, 0)));

    [Fact]
    public void The_memory_figure_is_out_of_the_engines_memory()
    {
        Assert.Equal("988 MB / 7.47 GB", StatsSummary.MemoryText(new StatsTotals(0, 8, 988L << 20, (long)(7.47 * (1L << 30)))));
        Assert.Equal("988 MB", StatsSummary.MemoryText(new StatsTotals(0, 0, 988L << 20, 0)));
    }

    private static ContainerRow Row(string id, RowState state) =>
        new(id, id, id, "nginx", state, "", [], DateTimeOffset.UnixEpoch, null);

    private static readonly Dictionary<string, StatsSample> Latest = new()
    {
        ["web"] = Sample(12.44, 0),
        ["old"] = Sample(3, 0),
    };

    [Fact]
    public void A_running_row_shows_its_cpu_with_one_decimal()
    {
        Assert.Equal(12.44, StatsSummary.CpuOf(Row("web", RowState.Running), Latest));
        Assert.Equal("12.4%", StatsSummary.CpuCell(Row("web", RowState.Running), Latest));
    }

    [Theory]
    [InlineData("old", RowState.Stopped)]
    [InlineData("old", RowState.Paused)]
    [InlineData("new", RowState.Running)]
    public void A_row_that_is_not_running_or_has_no_sample_yet_shows_no_cpu(string id, RowState state)
    {
        Assert.Null(StatsSummary.CpuOf(Row(id, state), Latest));
        Assert.Equal("", StatsSummary.CpuCell(Row(id, state), Latest));
    }

    [Fact]
    public void Without_stats_no_row_shows_cpu() =>
        Assert.Equal("", StatsSummary.CpuCell(Row("web", RowState.Running), null));
}

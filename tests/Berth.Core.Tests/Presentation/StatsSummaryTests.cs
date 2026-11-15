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
}

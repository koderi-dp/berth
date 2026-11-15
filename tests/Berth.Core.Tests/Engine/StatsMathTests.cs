using System.Text.Json;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class StatsMathTests
{
    // One line of /containers/{id}/stats from a 29.9.3 engine on cgroup v2, trimmed and with made-up values.
    internal const string Sample = """
        {
          "id": "abc",
          "name": "/shop-web-1",
          "read": "2026-11-15T22:51:14.95713468Z",
          "preread": "2026-11-15T22:51:13.950479694Z",
          "cpu_stats": {
            "cpu_usage": { "total_usage": 1600000000, "usage_in_kernelmode": 800000000, "usage_in_usermode": 800000000 },
            "system_cpu_usage": 500010000000000,
            "online_cpus": 4,
            "throttling_data": { "periods": 0, "throttled_periods": 0, "throttled_time": 0 }
          },
          "precpu_stats": {
            "cpu_usage": { "total_usage": 1100000000 },
            "system_cpu_usage": 500000000000000,
            "online_cpus": 4
          },
          "memory_stats": {
            "usage": 10506240,
            "stats": { "active_file": 0, "anon": 8650752, "inactive_file": 188416 },
            "limit": 27316191232
          },
          "networks": {
            "eth0": { "rx_bytes": 726, "rx_packets": 15, "tx_bytes": 126, "tx_packets": 3 },
            "eth1": { "rx_bytes": 1000, "tx_bytes": 2000 }
          },
          "pids_stats": { "current": 13, "limit": 31249 },
          "blkio_stats": {
            "io_service_bytes_recursive": [
              { "major": 8, "minor": 48, "op": "read", "value": 4390912 },
              { "major": 8, "minor": 48, "op": "write", "value": 4096 },
              { "major": 8, "minor": 64, "op": "Read", "value": 100 },
              { "major": 8, "minor": 64, "op": "Write", "value": 200 },
              { "major": 8, "minor": 64, "op": "Total", "value": 300 }
            ],
            "io_serviced_recursive": null
          }
        }
        """;

    [Fact]
    public void Cpu_percent_is_the_cpu_delta_over_the_system_delta_times_online_cpus()
    {
        // cpu delta 500,000,000; system delta 10,000,000,000; 4 CPUs: 0.05 * 4 * 100.
        Assert.Equal(20.0, ToSample(Sample).CpuPercent, precision: 10);
    }

    [Fact]
    public void Cpu_is_zero_when_the_system_delta_is_not_positive()
    {
        var same = ToSample("""
            {
              "cpu_stats": { "cpu_usage": { "total_usage": 2000 }, "system_cpu_usage": 9000, "online_cpus": 2 },
              "precpu_stats": { "cpu_usage": { "total_usage": 1000 }, "system_cpu_usage": 9000, "online_cpus": 2 }
            }
            """);
        var backwards = ToSample("""
            {
              "cpu_stats": { "cpu_usage": { "total_usage": 2000 }, "system_cpu_usage": 8000, "online_cpus": 2 },
              "precpu_stats": { "cpu_usage": { "total_usage": 1000 }, "system_cpu_usage": 9000, "online_cpus": 2 }
            }
            """);

        Assert.Equal(0, same.CpuPercent);
        Assert.Equal(0, backwards.CpuPercent);
    }

    [Fact]
    public void Cpu_is_zero_when_the_cpu_delta_is_negative()
    {
        var sample = ToSample("""
            {
              "cpu_stats": { "cpu_usage": { "total_usage": 1000 }, "system_cpu_usage": 20000, "online_cpus": 2 },
              "precpu_stats": { "cpu_usage": { "total_usage": 2000 }, "system_cpu_usage": 10000, "online_cpus": 2 }
            }
            """);

        Assert.Equal(0, sample.CpuPercent);
    }

    [Fact]
    public void Online_cpus_falls_back_to_the_per_cpu_list_then_one()
    {
        // cpu delta 1,000 over system delta 10,000 is 10% of one CPU.
        var perCpu = ToSample("""
            {
              "cpu_stats": { "cpu_usage": { "total_usage": 2000, "percpu_usage": [500, 500, 500] }, "system_cpu_usage": 20000 },
              "precpu_stats": { "cpu_usage": { "total_usage": 1000 }, "system_cpu_usage": 10000 }
            }
            """);
        var neither = ToSample("""
            {
              "cpu_stats": { "cpu_usage": { "total_usage": 2000 }, "system_cpu_usage": 20000, "online_cpus": 0 },
              "precpu_stats": { "cpu_usage": { "total_usage": 1000 }, "system_cpu_usage": 10000 }
            }
            """);

        Assert.Equal(30.0, perCpu.CpuPercent, precision: 10);
        Assert.Equal(10.0, neither.CpuPercent, precision: 10);
    }

    [Fact]
    public void The_first_sample_with_no_previous_reading_is_zero_percent()
    {
        // The first line of a stream has an empty precpu_stats: no system reading to take a delta from.
        var sample = ToSample("""
            {
              "read": "2026-11-15T22:51:14Z",
              "preread": "0001-01-01T00:00:00Z",
              "cpu_stats": { "cpu_usage": { "total_usage": 1600000000 }, "system_cpu_usage": 500010000000000, "online_cpus": 4 },
              "precpu_stats": { "cpu_usage": { "total_usage": 0 }, "throttling_data": { "periods": 0 } }
            }
            """);

        Assert.Equal(0, sample.CpuPercent);
    }

    [Fact]
    public void Memory_subtracts_inactive_file_on_cgroup_v2()
    {
        Assert.Equal(10506240 - 188416, ToSample(Sample).MemoryBytes);
    }

    [Fact]
    public void Memory_subtracts_total_inactive_file_on_cgroup_v1()
    {
        // v1 also carries this cgroup's own inactive_file; the hierarchical total_inactive_file is the one to subtract.
        var sample = ToSample("""
            { "memory_stats": { "usage": 50000, "stats": { "cache": 9000, "inactive_file": 1000, "total_inactive_file": 7000 }, "limit": 90000 } }
            """);

        Assert.Equal(43000, sample.MemoryBytes);
    }

    [Fact]
    public void Memory_is_the_usage_when_no_inactive_file_figure_is_given()
    {
        var noFigure = ToSample("""{ "memory_stats": { "usage": 50000, "stats": { "anon": 40000 } } }""");
        var noStats = ToSample("""{ "memory_stats": { "usage": 50000 } }""");

        Assert.Equal(50000, noFigure.MemoryBytes);
        Assert.Equal(50000, noStats.MemoryBytes);
    }

    [Fact]
    public void Memory_is_never_below_zero()
    {
        var sample = ToSample("""{ "memory_stats": { "usage": 1000, "stats": { "inactive_file": 4000 } } }""");

        Assert.Equal(0, sample.MemoryBytes);
    }

    [Fact]
    public void Network_sums_every_interface()
    {
        var sample = ToSample(Sample);

        Assert.Equal(726 + 1000, sample.NetworkReceivedBytes);
        Assert.Equal(126 + 2000, sample.NetworkSentBytes);
    }

    [Fact]
    public void Block_io_sums_reads_and_writes_by_op_in_any_case()
    {
        var sample = ToSample(Sample);

        Assert.Equal(4390912 + 100, sample.BlockReadBytes);
        Assert.Equal(4096 + 200, sample.BlockWrittenBytes);
    }

    [Fact]
    public void A_missing_section_is_zero()
    {
        // Only CPU figures: no memory, network, block I/O or pids sections.
        var sample = ToSample("""
            {
              "read": "2026-11-15T22:51:14Z",
              "cpu_stats": { "cpu_usage": { "total_usage": 2000 }, "system_cpu_usage": 20000, "online_cpus": 1 },
              "precpu_stats": { "cpu_usage": { "total_usage": 1000 }, "system_cpu_usage": 10000, "online_cpus": 1 }
            }
            """);

        Assert.Equal(
            new StatsSample(new DateTimeOffset(2026, 11, 15, 22, 51, 14, TimeSpan.Zero), 10.0, 0, 0, 0, 0, 0, 0, 0),
            sample);
    }

    [Fact]
    public void Time_limit_and_pids_are_read()
    {
        var sample = ToSample(Sample);

        Assert.Equal(new DateTimeOffset(2026, 11, 15, 22, 51, 14, TimeSpan.Zero).AddTicks(9571346), sample.Time);
        Assert.Equal(27316191232, sample.MemoryLimitBytes);
        Assert.Equal(13, sample.Pids);
    }

    [Theory]
    [InlineData("""
        {
          "read": "0001-01-01T00:00:00Z",
          "cpu_stats": { "cpu_usage": { "total_usage": 0, "percpu_usage": [] }, "system_cpu_usage": 0, "online_cpus": 0 },
          "precpu_stats": { "cpu_usage": { "total_usage": 0 }, "system_cpu_usage": 0, "online_cpus": 0 },
          "memory_stats": { "usage": 0, "stats": { "inactive_file": 0 }, "limit": 0 },
          "networks": { "eth0": { "rx_bytes": 0, "tx_bytes": 0 } },
          "pids_stats": { "current": 0 },
          "blkio_stats": { "io_service_bytes_recursive": [ { "op": "read", "value": 0 } ] }
        }
        """)]
    [InlineData("""
        {
          "read": null,
          "cpu_stats": { "cpu_usage": null, "system_cpu_usage": null, "online_cpus": null },
          "precpu_stats": null,
          "memory_stats": { "usage": null, "stats": null, "limit": null },
          "networks": { "eth0": null },
          "pids_stats": null,
          "blkio_stats": { "io_service_bytes_recursive": [ { "op": null, "value": null } ] }
        }
        """)]
    public void Nothing_is_ever_NaN_or_negative(string json)
    {
        var sample = ToSample(json);

        Assert.Equal(new StatsSample(DateTimeOffset.MinValue, 0, 0, 0, 0, 0, 0, 0, 0), sample);
        Assert.False(double.IsNaN(sample.CpuPercent));
    }

    internal static StatsSample ToSample(string json) =>
        StatsMath.ToSample(JsonSerializer.Deserialize(json, EngineJson.Default.WireStats)!);
}

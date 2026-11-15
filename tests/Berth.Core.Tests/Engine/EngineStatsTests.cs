using System.Text.Json.Nodes;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class EngineStatsTests
{
    private const string Path = "/v1.56/containers/abc/stats?stream=true";

    // The engine sends one sample per line.
    private static readonly string Line = JsonNode.Parse(StatsMathTests.Sample)!.ToJsonString() + "\n";

    private static async Task<(EngineClient Client, StubHandler Stub)> ConnectedAsync(ChunkStream body)
    {
        var stub = new StubHandler().WithVersion("1.56").On(Path, body);
        var client = new EngineClient(stub);
        await client.ConnectAsync(CancellationToken.None);
        return (client, stub);
    }

    [Fact]
    public async Task Request_asks_for_a_stream()
    {
        // The body stays open after the first sample: a buffered read or a deadline would never hand that sample over.
        var body = new ChunkStream().Write(Line);
        var (client, stub) = await ConnectedAsync(body);
        await using var _client = client;

        await using var samples = client.StreamStatsAsync("abc", CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await samples.MoveNextAsync());

        Assert.Equal(StatsMathTests.ToSample(StatsMathTests.Sample), samples.Current);
        Assert.Equal(("GET", Path), stub.Requests[^1]);
        body.End();
    }

    [Fact]
    public async Task A_malformed_line_is_skipped()
    {
        var body = new ChunkStream()
            .Write("{\"read\":\"2026-11-15T22:51:14Z\",\"cpu_st\n")
            .Write("not json at all\n")
            .Write("""{"memory_stats":{"usage":"lots"}}""" + "\n")
            .Write(Line)
            .End();
        var (client, _) = await ConnectedAsync(body);
        await using var _client = client;

        var samples = await ReadAllAsync(client.StreamStatsAsync("abc", CancellationToken.None));

        Assert.Equal([StatsMathTests.ToSample(StatsMathTests.Sample)], samples);
    }

    [Fact]
    public async Task The_stream_ending_ends_the_enumeration()
    {
        var body = new ChunkStream().Write(Line);
        var (client, _) = await ConnectedAsync(body);
        await using var _client = client;

        await using var samples = client.StreamStatsAsync("abc", CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await samples.MoveNextAsync());

        var next = samples.MoveNextAsync().AsTask();
        Assert.False(next.IsCompleted);
        body.End();

        Assert.False(await next);
    }

    // What the engine sends once the container has stopped: one line a second with no read time and empty sections, on a
    // connection it keeps open.
    private const string StoppedLine =
        """{"read":"0001-01-01T00:00:00Z","preread":"0001-01-01T00:00:00Z","cpu_stats":{"cpu_usage":{"total_usage":0},"throttling_data":{}},"precpu_stats":{"cpu_usage":{"total_usage":0},"throttling_data":{}},"memory_stats":{},"blkio_stats":{},"pids_stats":{}}""" + "\n";

    [Fact]
    public async Task The_container_stopping_ends_the_enumeration_although_the_engine_keeps_sending()
    {
        var second = Line.Replace("2026-11-15T22:51:14.95713468Z", "2026-11-15T22:51:15.95713468Z", StringComparison.Ordinal);
        var body = new ChunkStream().Write(Line).Write(second).Write(StoppedLine).Write(StoppedLine);
        var (client, _) = await ConnectedAsync(body);
        await using var _client = client;

        var samples = await ReadAllAsync(client.StreamStatsAsync("abc", CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, samples.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 22, 51, 14, TimeSpan.Zero).AddTicks(9571346), samples[0].Time);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 22, 51, 15, TimeSpan.Zero).AddTicks(9571346), samples[1].Time);
        body.End();
    }

    [Fact]
    public async Task A_stopped_container_yields_nothing()
    {
        // A line with no read time at all counts the same as the zero time.
        var body = new ChunkStream().Write(StoppedLine).Write("""{"memory_stats":{}}""" + "\n");
        var (client, _) = await ConnectedAsync(body);
        await using var _client = client;

        var samples = await ReadAllAsync(client.StreamStatsAsync("abc", CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(samples);
        body.End();
    }

    private static async Task<List<StatsSample>> ReadAllAsync(IAsyncEnumerable<StatsSample> samples)
    {
        var all = new List<StatsSample>();
        await foreach (var sample in samples)
        {
            all.Add(sample);
        }

        return all;
    }
}

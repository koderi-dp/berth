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

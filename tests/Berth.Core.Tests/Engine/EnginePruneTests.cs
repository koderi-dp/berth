using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

/// <summary>
/// A prune can run for minutes on a large engine, so it has no deadline: an answer that arrives after the plain deadline has passed
/// is still read. The plain deadline is shortened here so the test stays fast.
/// </summary>
public sealed class EnginePruneTests
{
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(50);

    private static async Task<EngineClient> ConnectedAsync(string path, ChunkStream body)
    {
        var client = new EngineClient(new StubHandler().WithVersion("1.56").On(path, body)) { PlainTimeout = ShortDeadline };
        await client.ConnectAsync(CancellationToken.None);
        return client;
    }

    [Theory]
    [InlineData("images", "/v1.56/images/prune?filters=%7B%22dangling%22%3A%5B%22true%22%5D%7D", """{"ImagesDeleted":[{"Deleted":"sha256:1"}],"SpaceReclaimed":7}""")]
    [InlineData("volumes", "/v1.56/volumes/prune?filters=%7B%22all%22%3A%5B%22true%22%5D%7D", """{"VolumesDeleted":["old_cache"],"SpaceReclaimed":7}""")]
    [InlineData("networks", "/v1.56/networks/prune", """{"NetworksDeleted":["shop_default"]}""")]
    public async Task A_prune_answer_that_arrives_after_the_plain_deadline_is_still_read(string kind, string path, string answer)
    {
        var body = new ChunkStream();
        await using var client = await ConnectedAsync(path, body);

        var pending = Prune(client, kind);
        await Task.Delay(ShortDeadline * 4);
        body.Write(answer).End();

        var result = await pending;
        Assert.Single(result.Deleted);
    }

    [Fact]
    public async Task A_plain_call_answered_after_the_deadline_fails_so_the_prune_tests_mean_something()
    {
        var body = new ChunkStream();
        await using var client = await ConnectedAsync("/v1.56/images/json", body);

        var pending = client.ListImagesAsync(CancellationToken.None);
        await Task.Delay(ShortDeadline * 4);
        body.Write("[]").End();

        var error = await Assert.ThrowsAsync<EngineException>(() => pending);
        Assert.Equal(0, error.StatusCode);
    }

    private static Task<PruneResult> Prune(EngineClient client, string kind) => kind switch
    {
        "images" => client.PruneImagesAsync(danglingOnly: true, CancellationToken.None),
        "volumes" => client.PruneVolumesAsync(CancellationToken.None),
        "networks" => client.PruneNetworksAsync(CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

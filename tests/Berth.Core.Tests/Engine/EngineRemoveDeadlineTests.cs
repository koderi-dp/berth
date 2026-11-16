using Berth.Engine;

namespace Berth.Tests.Engine;

/// <summary>
/// Removing a large image or a full volume can take longer than a plain call's deadline, so image and volume removes have the long
/// deadline stop and restart have. Both deadlines are shortened here so the tests stay fast.
/// </summary>
public sealed class EngineRemoveDeadlineTests
{
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(50);

    private static async Task<EngineClient> ConnectedAsync(string path, ChunkStream body, TimeSpan longDeadline)
    {
        var client = new EngineClient(new StubHandler().WithVersion("1.56").On(path, body)) { PlainTimeout = ShortDeadline, LongTimeout = longDeadline };
        await client.ConnectAsync(CancellationToken.None);
        return client;
    }

    [Theory]
    [InlineData("image", "/v1.56/images/big", "[]")]
    [InlineData("volume", "/v1.56/volumes/big", "")]
    public async Task A_remove_answered_after_the_plain_deadline_is_still_read(string kind, string path, string answer)
    {
        var body = new ChunkStream();
        await using var client = await ConnectedAsync(path, body, TimeSpan.FromSeconds(30));

        var pending = Remove(client, kind);
        await Task.Delay(ShortDeadline * 4);
        body.Write(answer).End();

        await pending;
    }

    [Theory]
    [InlineData("image", "/v1.56/images/big")]
    [InlineData("volume", "/v1.56/volumes/big")]
    public async Task A_remove_that_outlasts_the_long_deadline_says_it_timed_out(string kind, string path)
    {
        var body = new ChunkStream();
        await using var client = await ConnectedAsync(path, body, ShortDeadline);

        var error = await Assert.ThrowsAsync<EngineException>(() => Remove(client, kind));

        Assert.Equal(0, error.StatusCode);
        Assert.True(error.TimedOut);
        Assert.StartsWith("The engine did not answer within", error.Message, StringComparison.Ordinal);
    }

    private static Task Remove(EngineClient client, string kind) => kind == "image"
        ? client.RemoveImageAsync("big", CancellationToken.None)
        : client.RemoveVolumeAsync("big", CancellationToken.None);
}

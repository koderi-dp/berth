using System.Net;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class EngineVolumesTests
{
    // The shape of a /volumes answer from a 29.9.3 engine, with made-up values: a named volume, an anonymous one, and one with its
    // optional fields null.
    private const string ThreeVolumes = """
        {
          "Volumes": [
            {
              "CreatedAt": "2026-11-15T17:53:20+01:00",
              "Driver": "local",
              "Labels": { "com.docker.compose.project": "shop", "com.docker.compose.volume": "data" },
              "Mountpoint": "/var/lib/docker/volumes/shop_data/_data",
              "Name": "shop_data",
              "Options": null,
              "Scope": "local"
            },
            {
              "CreatedAt": "2026-11-15T17:53:19+01:00",
              "Driver": "local",
              "Labels": { "com.docker.volume.anonymous": "" },
              "Mountpoint": "/var/lib/docker/volumes/0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0/_data",
              "Name": "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0",
              "Options": null,
              "Scope": "local"
            },
            { "Name": "bare", "Driver": null, "Labels": null, "Mountpoint": null, "CreatedAt": null, "Scope": null }
          ],
          "Warnings": null
        }
        """;

    private const string PruneAll = "/v1.56/volumes/prune?filters=%7B%22all%22%3A%5B%22true%22%5D%7D";

    private static async Task<(EngineClient Client, StubHandler Stub)> ConnectedAsync(Action<StubHandler> setup)
    {
        var stub = new StubHandler().WithVersion("1.56");
        setup(stub);
        var client = new EngineClient(stub);
        await client.ConnectAsync(CancellationToken.None);
        return (client, stub);
    }

    [Fact]
    public async Task List_maps_each_volume()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/volumes", ThreeVolumes));
        await using var _client = client;

        var volumes = await client.ListVolumesAsync(CancellationToken.None);

        Assert.Equal(("GET", "/v1.56/volumes"), stub.Requests[^1]);
        Assert.Equal(3, volumes.Count);
        var data = volumes[0];
        Assert.Equal("shop_data", data.Name);
        Assert.Equal("local", data.Driver);
        Assert.Equal("/var/lib/docker/volumes/shop_data/_data", data.Mountpoint);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 17, 53, 20, TimeSpan.FromHours(1)), data.CreatedAt);
        Assert.Equal("shop", data.Labels["com.docker.compose.project"]);
        Assert.Equal("local", data.Scope);
        Assert.True(volumes[1].Labels.ContainsKey("com.docker.volume.anonymous"));
    }

    [Fact]
    public async Task List_tolerates_null_fields_and_a_null_list()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/volumes", ThreeVolumes));
        await using var _client = client;

        var bare = (await client.ListVolumesAsync(CancellationToken.None))[2];

        Assert.Equal("bare", bare.Name);
        Assert.Equal("", bare.Driver);
        Assert.Equal("", bare.Mountpoint);
        Assert.Null(bare.CreatedAt);
        Assert.Empty(bare.Labels);
        Assert.Equal("", bare.Scope);

        await using var empty = (await ConnectedAsync(stub => stub.On("/v1.56/volumes", """{"Volumes":null,"Warnings":null}"""))).Client;
        Assert.Empty(await empty.ListVolumesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Create_posts_the_name_and_maps_the_answer()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On(
            "/v1.56/volumes/create",
            HttpStatusCode.Created,
            """{"CreatedAt":"2026-11-16T06:00:00Z","Driver":"local","Labels":{},"Mountpoint":"/var/lib/docker/volumes/berth-check-vol/_data","Name":"berth-check-vol","Options":null,"Scope":"local"}"""));
        await using var _client = client;

        var volume = await client.CreateVolumeAsync("berth-check-vol", CancellationToken.None);

        Assert.Equal(("POST", "/v1.56/volumes/create"), stub.Requests[^1]);
        Assert.Equal("""{"Name":"berth-check-vol"}""", stub.Bodies[^1]);
        Assert.Equal("berth-check-vol", volume.Name);
        Assert.Equal("/var/lib/docker/volumes/berth-check-vol/_data", volume.Mountpoint);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 6, 0, 0, TimeSpan.Zero), volume.CreatedAt);
    }

    [Fact]
    public async Task Remove_escapes_the_name_and_sends_force_when_asked()
    {
        var (client, stub) = await ConnectedAsync(stub => stub
            .On("/v1.56/volumes/shop_data", HttpStatusCode.NoContent, "")
            .On("/v1.56/volumes/shop_data?force=true", HttpStatusCode.NoContent, ""));
        await using var _client = client;

        await client.RemoveVolumeAsync("shop_data", force: false, CancellationToken.None);
        Assert.Equal(("DELETE", "/v1.56/volumes/shop_data"), stub.Requests[^1]);

        await client.RemoveVolumeAsync("shop_data", force: true, CancellationToken.None);
        Assert.Equal(("DELETE", "/v1.56/volumes/shop_data?force=true"), stub.Requests[^1]);
    }

    [Theory]
    [InlineData(404, "get berth-check-nope: no such volume")]
    [InlineData(409, "remove berth-check-nope: volume is in use - [1f0c2a9e8b7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3]")]
    public async Task Remove_carries_the_engines_message(int status, string message)
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/volumes/berth-check-nope", (HttpStatusCode)status, $$"""{"message":"{{message}}"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.RemoveVolumeAsync("berth-check-nope", force: false, CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task Prune_removes_every_unused_volume_and_lists_them()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On(PruneAll, """{"VolumesDeleted":["shop_data","bare"],"SpaceReclaimed":9298}"""));
        await using var _client = client;

        var result = await client.PruneVolumesAsync(CancellationToken.None);

        Assert.Equal(("POST", PruneAll), stub.Requests[^1]);
        Assert.Equal(["shop_data", "bare"], result.Deleted);
        Assert.Equal(9298, result.SpaceReclaimed);
    }

    [Fact]
    public async Task Prune_tolerates_a_null_list()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(PruneAll, """{"VolumesDeleted":null,"SpaceReclaimed":0}"""));
        await using var _client = client;

        var result = await client.PruneVolumesAsync(CancellationToken.None);

        Assert.Empty(result.Deleted);
        Assert.Equal(0, result.SpaceReclaimed);
    }
}

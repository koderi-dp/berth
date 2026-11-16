using System.Net;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class EngineDiskUsageTests
{
    // From API 1.52 Berth asks with verbose=true, so the engine sends each item once, inside its totals block.
    private const string Path = "/v1.56/system/df?type=image&type=volume&verbose=true";

    // What an engine from API 1.52 on sends with verbose=true: its own totals with their items, no older lists; made-up values.
    private const string WithTotals = """
        {
          "ImageUsage": {
            "ActiveCount": 1, "Reclaimable": 400, "TotalCount": 2, "TotalSize": 1000,
            "Items": [
              { "Containers": 1, "Created": 1794700000, "Id": "sha256:a", "RepoTags": ["alpine:3.19"], "SharedSize": 100, "Size": 600 },
              { "Containers": 0, "Created": 1794600000, "Id": "sha256:b", "RepoTags": ["nginx:1.29"], "SharedSize": 100, "Size": 500 }
            ]
          },
          "VolumeUsage": {
            "ActiveCount": 1, "Reclaimable": 200, "TotalCount": 2, "TotalSize": 500,
            "Items": [
              { "Name": "shop_data", "Driver": "local", "UsageData": { "RefCount": 1, "Size": 300 } },
              { "Name": "old_cache", "Driver": "local", "UsageData": { "RefCount": 0, "Size": 200 } }
            ]
          }
        }
        """;

    // What the same engine sends without verbose: the older lists beside the totals blocks, every item twice.
    private const string BothCopies = """
        {
          "LayersSize": 1000,
          "Images": [
            { "Containers": 1, "Created": 1794700000, "Id": "sha256:a", "RepoTags": ["alpine:3.19"], "SharedSize": 100, "Size": 600 },
            { "Containers": 0, "Created": 1794600000, "Id": "sha256:b", "RepoTags": ["nginx:1.29"], "SharedSize": 100, "Size": 500 }
          ],
          "Volumes": [
            { "Name": "shop_data", "Driver": "local", "UsageData": { "RefCount": 1, "Size": 300 } },
            { "Name": "old_cache", "Driver": "local", "UsageData": { "RefCount": 0, "Size": 200 } }
          ],
          "ImageUsage": {
            "ActiveCount": 1, "Reclaimable": 400, "TotalCount": 2, "TotalSize": 1000,
            "Items": [
              { "Containers": 1, "Created": 1794700000, "Id": "sha256:a", "RepoTags": ["alpine:3.19"], "SharedSize": 100, "Size": 600 },
              { "Containers": 0, "Created": 1794600000, "Id": "sha256:b", "RepoTags": ["nginx:1.29"], "SharedSize": 100, "Size": 500 }
            ]
          },
          "VolumeUsage": {
            "ActiveCount": 1, "Reclaimable": 200, "TotalCount": 2, "TotalSize": 500,
            "Items": [
              { "Name": "shop_data", "Driver": "local", "UsageData": { "RefCount": 1, "Size": 300 } },
              { "Name": "old_cache", "Driver": "local", "UsageData": { "RefCount": 0, "Size": 200 } }
            ]
          }
        }
        """;

    // An engine before API 1.52 sends only the lists; a size of -1 means "not computed".
    private const string ListsOnly = """
        {
          "LayersSize": 1000,
          "Images": [
            { "Containers": 1, "Id": "sha256:a", "SharedSize": 100, "Size": 600 },
            { "Containers": 0, "Id": "sha256:b", "SharedSize": 100, "Size": 500 },
            { "Containers": 0, "Id": "sha256:c", "SharedSize": -1, "Size": 50 }
          ],
          "Volumes": [
            { "Name": "shop_data", "UsageData": { "RefCount": 1, "Size": 300 } },
            { "Name": "old_cache", "UsageData": { "RefCount": 0, "Size": 200 } },
            { "Name": "remote", "UsageData": { "RefCount": 0, "Size": -1 } },
            { "Name": "unmeasured", "UsageData": null }
          ]
        }
        """;

    private static async Task<(EngineClient Client, StubHandler Stub)> ConnectedAsync(Action<StubHandler> setup, string version = "1.56")
    {
        var stub = new StubHandler().WithVersion(version);
        setup(stub);
        var client = new EngineClient(stub);
        await client.ConnectAsync(CancellationToken.None);
        return (client, stub);
    }

    [Theory]
    [InlineData("1.52")]
    [InlineData("1.56")]
    public async Task Disk_usage_from_API_1_52_asks_for_each_item_once_and_takes_the_engines_totals(string version)
    {
        var path = $"/v{version}/system/df?type=image&type=volume&verbose=true";
        var (client, stub) = await ConnectedAsync(stub => stub.On(path, WithTotals), version);
        await using var _client = client;

        var usage = await client.GetDiskUsageAsync(CancellationToken.None);

        Assert.Equal(("GET", path), stub.Requests[^1]);
        Assert.Equal([new ImageDiskUsage("sha256:a", 600, 100, 500, 1), new ImageDiskUsage("sha256:b", 500, 100, 400, 0)], usage.Images);
        Assert.Equal(new UsageTotals(2, 1, 1000, 400), usage.ImageTotals);
        Assert.Equal([new VolumeDiskUsage("shop_data", 300, 1), new VolumeDiskUsage("old_cache", 200, 0)], usage.Volumes);
        Assert.Equal(new UsageTotals(2, 1, 500, 200), usage.VolumeTotals);
    }

    [Fact]
    public async Task Disk_usage_reads_one_copy_of_each_item_when_the_engine_sends_both()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(Path, BothCopies));
        await using var _client = client;

        var usage = await client.GetDiskUsageAsync(CancellationToken.None);

        Assert.Equal(["sha256:a", "sha256:b"], usage.Images.Select(i => i.Id));
        Assert.Equal(["shop_data", "old_cache"], usage.Volumes.Select(v => v.Name));
        Assert.Equal(new UsageTotals(2, 1, 1000, 400), usage.ImageTotals);
    }

    [Theory]
    [InlineData("1.44")]
    [InlineData("1.51")]
    public async Task Disk_usage_of_an_engine_before_API_1_52_asks_for_the_lists_and_computes_the_totals_as_the_docker_CLI_does(string version)
    {
        var path = $"/v{version}/system/df?type=image&type=volume";
        var (client, stub) = await ConnectedAsync(stub => stub.On(path, ListsOnly), version);
        await using var _client = client;

        var usage = await client.GetDiskUsageAsync(CancellationToken.None);

        Assert.Equal(("GET", path), stub.Requests[^1]);

        Assert.Equal(new ImageDiskUsage("sha256:c", 50, null, null, 0), usage.Images[2]);
        // Total is the layers' size; reclaimable is that minus what images in use hold alone (600 - 100).
        Assert.Equal(new UsageTotals(3, 1, 1000, 500), usage.ImageTotals);
        Assert.Equal(
            [
                new VolumeDiskUsage("shop_data", 300, 1),
                new VolumeDiskUsage("old_cache", 200, 0),
                new VolumeDiskUsage("remote", null, 0),
                new VolumeDiskUsage("unmeasured", null, 0),
            ],
            usage.Volumes);
        // Sizes not measured count for nothing.
        Assert.Equal(new UsageTotals(4, 1, 500, 200), usage.VolumeTotals);
    }

    [Fact]
    public async Task Disk_usage_tolerates_an_empty_answer()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(Path, """{"Images":null,"Volumes":null}"""));
        await using var _client = client;

        var usage = await client.GetDiskUsageAsync(CancellationToken.None);

        Assert.Empty(usage.Images);
        Assert.Equal(new UsageTotals(0, 0, 0, 0), usage.ImageTotals);
        Assert.Empty(usage.Volumes);
        Assert.Equal(new UsageTotals(0, 0, 0, 0), usage.VolumeTotals);
    }

    [Fact]
    public async Task Disk_usage_waits_for_an_answer_that_arrives_in_pieces()
    {
        var body = new ChunkStream().Write(WithTotals[..40]);
        var (client, _) = await ConnectedAsync(stub => stub.On(Path, body));
        await using var _client = client;

        var pending = client.GetDiskUsageAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
        body.Write(WithTotals[40..]).End();

        Assert.Equal(2, (await pending).Images.Count);
    }

    [Fact]
    public async Task A_dropped_connection_mid_answer_throws_EngineException_with_status_0()
    {
        var body = new ChunkStream().Write(WithTotals[..40]);
        var (client, _) = await ConnectedAsync(stub => stub.On(Path, body));
        await using var _client = client;

        var pending = client.GetDiskUsageAsync(CancellationToken.None);
        body.Fail(new IOException("The connection was reset."));

        var error = await Assert.ThrowsAsync<EngineException>(() => pending);
        Assert.Equal(0, error.StatusCode);
        Assert.Equal("The connection was reset.", error.Message);
    }

    [Fact]
    public async Task Cancelling_while_the_answer_arrives_throws_OperationCanceledException()
    {
        var body = new ChunkStream().Write(WithTotals[..40]);
        var (client, _) = await ConnectedAsync(stub => stub.On(Path, body));
        await using var _client = client;
        using var cancel = new CancellationTokenSource();

        var pending = client.GetDiskUsageAsync(cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        body.End();
    }

    [Fact]
    public async Task An_error_carries_the_engines_own_message()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            Path, HttpStatusCode.InternalServerError, """{"message":"a disk usage operation is already running"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.GetDiskUsageAsync(CancellationToken.None));

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("a disk usage operation is already running", error.Message);
    }
}

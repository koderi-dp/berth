using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Session;

/// <summary>The fake's scripting of images, volumes, networks, disk use and create, which later tests rely on.</summary>
public sealed class FakeEngineApiTests
{
    [Fact]
    public async Task A_pull_yields_what_the_test_pushes_and_ends_when_it_ends()
    {
        var api = new FakeEngineApi();
        api.Pulls.Push(new PullProgress("a1", "Downloading", 1, 2));
        api.Pulls.Push(new PullProgress("", "Status: Downloaded newer image for alpine:latest", null, null));
        api.Pulls.End();

        var lines = new List<PullProgress>();
        await foreach (var line in api.PullImageAsync("alpine", CancellationToken.None))
            lines.Add(line);

        Assert.Equal(2, lines.Count);
        Assert.Contains("Pull alpine", api.Calls);
    }

    [Fact]
    public async Task A_pull_fails_with_the_error_the_test_ends_it_with()
    {
        var api = new FakeEngineApi();
        api.Pulls.Push(new PullProgress("a1", "Downloading", 1, 2));
        api.Pulls.End(new EngineException(200, "manifest unknown"));

        var seen = 0;
        var error = await Assert.ThrowsAsync<EngineException>(async () =>
        {
            await foreach (var _ in api.PullImageAsync("alpine", CancellationToken.None))
                seen++;
        });

        Assert.Equal(1, seen);
        Assert.Equal("manifest unknown", error.Message);
    }

    [Fact]
    public async Task Each_call_answers_what_the_test_set_and_fails_when_told()
    {
        var image = new ImageSummary("sha256:a", ["alpine:latest"], [], 1, 2, 0);
        var prune = new PruneResult(["sha256:a"], 2);
        var api = new FakeEngineApi { Images = [image], Prune = prune, CreatedId = "new" };
        var request = new ContainerCreateRequest("alpine", "berth-it-x", [], [], []);

        Assert.Equal([image], await api.ListImagesAsync(CancellationToken.None));
        Assert.Same(prune, await api.PruneImagesAsync(danglingOnly: false, CancellationToken.None));
        Assert.Equal("new", await api.CreateContainerAsync(request, CancellationToken.None));
        Assert.Equal([request], api.CreateRequests);
        Assert.Equal(404, (await Assert.ThrowsAsync<EngineException>(() => api.InspectImageAsync("nope", CancellationToken.None))).StatusCode);

        api.Fail("RemoveVolume", new EngineException(409, "volume is in use"));
        var error = await Assert.ThrowsAsync<EngineException>(() => api.RemoveVolumeAsync("v", force: false, CancellationToken.None));

        Assert.Equal("volume is in use", error.Message);
        Assert.Equal(
            ["ListImages", "PruneImages danglingOnly=False", "CreateContainer alpine name=berth-it-x", "InspectImage nope", "RemoveVolume v force=False"],
            api.Calls);
    }
}

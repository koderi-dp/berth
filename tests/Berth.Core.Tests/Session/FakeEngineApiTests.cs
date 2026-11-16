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
    public async Task Pulls_of_two_references_are_scripted_apart()
    {
        var api = new FakeEngineApi();
        var nginx = api.PullOf("nginx:1.29");
        var alpine = api.PullOf("alpine");

        await using var nginxLines = api.PullImageAsync("nginx:1.29", CancellationToken.None).GetAsyncEnumerator();
        await using var alpineLines = api.PullImageAsync("alpine", CancellationToken.None).GetAsyncEnumerator();
        nginx.Push(new PullProgress("n1", "Downloading", 1, 2));
        alpine.End(new EngineException(200, "manifest unknown"));
        nginx.End();

        var patience = TimeSpan.FromSeconds(10);
        Assert.True(await nginxLines.MoveNextAsync().AsTask().WaitAsync(patience));
        Assert.Equal("n1", nginxLines.Current.LayerId);
        Assert.False(await nginxLines.MoveNextAsync().AsTask().WaitAsync(patience));
        var error = await Assert.ThrowsAsync<EngineException>(() => alpineLines.MoveNextAsync().AsTask().WaitAsync(patience));
        Assert.Equal("manifest unknown", error.Message);
        Assert.Equal(0, api.Pulls.Opened);
    }

    [Fact]
    public async Task Each_call_answers_what_the_test_set_and_fails_when_told()
    {
        var image = new ImageSummary("sha256:a", ["alpine:latest"], [], 1, 2, 0);
        var api = new FakeEngineApi { Images = [image], CreatedId = "new" };
        var request = new ContainerCreateRequest("alpine", "berth-it-x", [], [], []);

        Assert.Equal([image], await api.ListImagesAsync(CancellationToken.None));
        Assert.Equal("new", await api.CreateContainerAsync(request, CancellationToken.None));
        Assert.Equal([request], api.CreateRequests);
        Assert.Equal(404, (await Assert.ThrowsAsync<EngineException>(() => api.InspectImageAsync("nope", CancellationToken.None))).StatusCode);

        api.Fail("RemoveVolume", new EngineException(409, "volume is in use"));
        var error = await Assert.ThrowsAsync<EngineException>(() => api.RemoveVolumeAsync("v", CancellationToken.None));

        Assert.Equal("volume is in use", error.Message);
        Assert.Equal(
            ["ListImages", "CreateContainer alpine name=berth-it-x", "InspectImage nope", "RemoveVolume v"],
            api.Calls);
    }

    [Fact]
    public async Task An_exec_start_hands_out_a_shell_that_carries_bytes_both_ways_and_ends()
    {
        var api = new FakeEngineApi { ExecId = "e7" };
        var size = new TerminalSize(80, 24);

        var execId = await api.CreateExecAsync("a", ["sh"], size, CancellationToken.None);
        var stream = await api.StartExecAsync(execId, size, CancellationToken.None);
        var shell = await api.NextShellAsync();
        await stream.WriteAsync(new byte[] { 0x03, 0xFF });
        await shell.WriteOutputAsync([0x1B, 0x5B]);
        shell.End();
        var output = new MemoryStream();
        await stream.CopyToAsync(output);
        await stream.DisposeAsync();

        Assert.Equal("e7", execId);
        Assert.Equal([0x1B, 0x5B], output.ToArray());
        Assert.Equal([0x03, 0xFF], await shell.ReadAllInputAsync());
        await shell.Disposed;
        Assert.Equal(["CreateExec a", "StartExec e7"], api.Calls);
        var create = Assert.Single(api.ExecCreates);
        Assert.Equal("a", create.ContainerId);
        Assert.Equal(["sh"], create.Command);
        Assert.Equal(size, create.Size);
    }

    [Fact]
    public async Task Exec_resizes_are_recorded_and_inspections_answer_in_order_then_repeat_the_last()
    {
        var api = new FakeEngineApi();
        api.ScriptExecInspections(new ExecInspection(true, null), new ExecInspection(false, 3));

        await api.ResizeExecAsync("e1", new TerminalSize(120, 30), CancellationToken.None);
        var answers = new List<ExecInspection>();
        for (var i = 0; i < 3; i++)
            answers.Add(await api.InspectExecAsync("e1", CancellationToken.None));

        Assert.Equal(("e1", new TerminalSize(120, 30)), Assert.Single(api.Resizes));
        Assert.Equal([new(true, null), new(false, 3), new(false, 3)], answers);
    }
}

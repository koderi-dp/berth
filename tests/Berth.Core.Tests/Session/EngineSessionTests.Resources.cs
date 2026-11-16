using Berth.Engine;
using Berth.Engine.Models;
using Berth.Session;
using Berth.State;

namespace Berth.Tests.Session;

/// <summary>The runtime of the images, volumes and networks lists, their actions, disk use, pulls and running a container.</summary>
public sealed partial class EngineSessionTests
{
    private static ImageSummary Image(string id, params string[] tags) => new(id, tags, [], 1_700_000_000, 1000, 0);

    private static VolumeSummary Volume(string name) => new(name, "local", $"/v/{name}", null, new Dictionary<string, string>(), "local");

    private static NetworkSummary Network(string id) => new(id, id, "bridge", "local", null, false, new Dictionary<string, string>(), []);

    private static PullProgress Line(string layer, long current) => new(layer, "Downloading", current, 100);

    [Fact]
    public async Task Each_list_is_read_when_its_page_is_first_shown()
    {
        var api = new FakeEngineApi
        {
            Images = [Image("sha256:a", "nginx:latest")],
            Volumes = [Volume("data")],
            Networks = [Network("bridge")],
        };
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        Assert.DoesNotContain("ListImages", api.Calls);

        h.Session.Dispatch(new ImagesWanted());
        h.Session.Dispatch(new VolumesWanted());
        h.Session.Dispatch(new NetworksWanted());
        var state = await h.WaitFor(s => s.Images.Loaded && s.Volumes.Loaded && s.Networks.Loaded);

        Assert.Equal(["sha256:a"], state.Images.Items.Select(i => i.Id));
        Assert.Equal(["data"], state.Volumes.Items.Select(v => v.Name));
        Assert.Equal(["bridge"], state.Networks.Items.Select(n => n.Id));
    }

    [Theory]
    [InlineData("image", "tag")]
    [InlineData("volume", "create")]
    [InlineData("network", "connect")]
    public async Task A_list_reloads_after_the_debounce_on_its_engine_events(string type, string action)
    {
        var api = new FakeEngineApi();
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        h.Session.Dispatch(new ImagesWanted());
        h.Session.Dispatch(new VolumesWanted());
        h.Session.Dispatch(new NetworksWanted());
        await h.WaitFor(s => s.Images.Loaded && s.Volumes.Loaded && s.Networks.Loaded);

        api.Images = [Image("sha256:new", "x:1")];
        api.Volumes = [Volume("new")];
        api.Networks = [Network("new")];
        api.Push(Event(type, action));
        await h.WaitFor(s => s.Images.Status.ReloadScheduled || s.Volumes.Status.ReloadScheduled || s.Networks.Status.ReloadScheduled);
        h.Time.Advance(SessionReducer.ReloadDebounce);

        var state = type switch
        {
            "image" => await h.WaitFor(s => s.Images.Items.Length == 1),
            "volume" => await h.WaitFor(s => s.Volumes.Items.Length == 1),
            _ => await h.WaitFor(s => s.Networks.Items.Length == 1),
        };
        Assert.Equal(type == "image" ? 1 : 0, state.Images.Items.Length);
        Assert.Equal(type == "volume" ? 1 : 0, state.Volumes.Items.Length);
        Assert.Equal(type == "network" ? 1 : 0, state.Networks.Items.Length);
    }

    [Fact]
    public async Task Disk_use_is_not_read_until_asked_for()
    {
        var api = new FakeEngineApi { Images = [Image("sha256:a", "nginx:latest")] };
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        h.Session.Dispatch(new ImagesWanted());
        await h.WaitFor(s => s.Images.Loaded);
        h.Session.Dispatch(new RemoveRequested(ResourceKind.Images, [new RemovalItem("sha256:a")]));
        await h.WaitFor(s => s.Images.Removal is { IsRunning: false } && s.Images.Status.ReloadScheduled);
        h.Time.Advance(SessionReducer.ReloadDebounce);
        await h.WaitFor(s => !s.Images.Status.ReloadScheduled);

        Assert.DoesNotContain("DiskUsage", api.Calls);

        api.DiskUsage = new DiskUsage([], new UsageTotals(1, 1, 42, 0), [], new UsageTotals(0, 0, 0, 0));
        h.Session.Dispatch(new DiskUsageRequested());
        var state = await h.WaitFor(s => s.DiskUsage.Usage is not null);

        Assert.Equal(42, state.DiskUsage.Usage!.ImageTotals.TotalSize);
        Assert.Single(api.Calls, c => c == "DiskUsage");
    }

    [Fact]
    public async Task Removes_and_creates_reach_the_engine_and_an_images_tags_go_in_order_never_forced()
    {
        var api = new FakeEngineApi { Images = [Image("sha256:a", "a:1", "a:2", "a:3")], Volumes = [Volume("data")], Networks = [Network("n1")] };
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        await AllWantedAsync(h);

        h.Session.Dispatch(new RemoveRequested(ResourceKind.Images, [new RemovalItem("sha256:a", ["a:1", "a:2", "a:3"])]));
        h.Session.Dispatch(new RemoveRequested(ResourceKind.Volumes, [new RemovalItem("data")]));
        h.Session.Dispatch(new RemoveRequested(ResourceKind.Networks, [new RemovalItem("n1")]));
        h.Session.Dispatch(new CreateVolumeRequested("berth-it-v"));
        var state = await h.WaitFor(s =>
            s.Images.Removal is { IsRunning: false } && s.Volumes.Removal is { IsRunning: false } && s.Networks.Removal is { IsRunning: false }
            && s.Volumes.Pending.IsEmpty);

        Assert.Equal(["RemoveImage a:1 force=False", "RemoveImage a:2 force=False", "RemoveImage a:3 force=False"], api.Calls.Where(c => c.StartsWith("RemoveImage", StringComparison.Ordinal)));
        Assert.Contains("RemoveVolume data force=False", api.Calls);
        Assert.Contains("RemoveNetwork n1", api.Calls);
        Assert.Contains("CreateVolume berth-it-v", api.Calls);
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("Prune", StringComparison.Ordinal));
        Assert.Empty(state.Images.Removal!.Refusals);
        Assert.Null(state.ActionError);
    }

    [Fact]
    public async Task A_volume_create_ends_as_created_or_as_the_engine_refused_it()
    {
        var api = new FakeEngineApi { Volumes = [Volume("data")] };
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        await AllWantedAsync(h);

        h.Session.Dispatch(new CreateVolumeRequested("berth-it-v"));
        var created = await h.WaitFor(s => s.VolumeCreate is { Status: VolumeCreateStatus.Created });
        Assert.Equal("berth-it-v", created.VolumeCreate!.Name);

        h.Session.Dispatch(new VolumeCreateDismissed());
        api.Fail("CreateVolume", new EngineException(500, "error while creating volume root path: file name too long"));
        h.Session.Dispatch(new CreateVolumeRequested("berth-it-w"));
        var refused = await h.WaitFor(s => s.VolumeCreate is { Status: VolumeCreateStatus.Refused });

        Assert.Equal(new VolumeCreate("berth-it-w", VolumeCreateStatus.Refused, "error while creating volume root path: file name too long"), refused.VolumeCreate);
        Assert.Null(refused.ActionError);
    }

    [Fact]
    public async Task An_image_item_stops_at_the_first_tag_the_engine_refuses()
    {
        var api = new FakeEngineApi { Images = [Image("sha256:a", "a:1", "a:2")] };
        api.Fail("RemoveImage", new EngineException(409, "conflict: unable to remove repository reference \"a:1\" (must force) - container c1 is using its referenced image"));
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        await AllWantedAsync(h);
        h.Session.Dispatch(new RemoveRequested(ResourceKind.Images, [new RemovalItem("sha256:a", ["a:1", "a:2"])]));
        var state = await h.WaitFor(s => s.Images.Removal is { IsRunning: false });

        Assert.Equal(["RemoveImage a:1 force=False"], api.Calls.Where(c => c.StartsWith("RemoveImage", StringComparison.Ordinal)));
        var refusal = Assert.Single(state.Images.Removal!.Refusals);
        Assert.Equal("a:1", refusal.Key);
        Assert.Equal("conflict: unable to remove repository reference \"a:1\" (must force) - container c1 is using its referenced image", refusal.Message);
    }

    [Fact]
    public async Task A_batch_sends_four_at_a_time_and_reads_the_disk_use_once_at_its_end()
    {
        var api = new FakeEngineApi { Volumes = [.. Enumerable.Range(1, 6).Select(i => Volume($"v{i}"))] };
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        await AllWantedAsync(h);
        h.Session.Dispatch(new DiskUsageRequested());
        await h.WaitFor(s => s.DiskUsage.Usage is not null && !s.DiskUsage.Loading);
        var held = api.Hold("RemoveVolume");

        h.Session.Dispatch(new RemoveRequested(ResourceKind.Volumes, [.. Enumerable.Range(1, 6).Select(i => new RemovalItem($"v{i}"))]));
        await held.Entered.WaitAsync(Patience);
        var sending = await h.WaitFor(s => s.Volumes.Removal is { Running: 4 });
        Assert.Equal(2, sending.Volumes.Removal!.Queued.Length);

        held.Release();
        await h.WaitFor(s => s.Volumes.Removal is { IsRunning: false } && !s.DiskUsage.Loading);

        Assert.Equal(6, api.Calls.Count(c => c.StartsWith("RemoveVolume", StringComparison.Ordinal)));
        Assert.Equal(2, api.Calls.Count(c => c == "DiskUsage"));
    }

    [Fact]
    public async Task A_remove_that_outlasts_its_deadline_is_that_items_failure_and_the_batch_and_connection_go_on()
    {
        var api = new FakeEngineApi { Volumes = [Volume("big"), Volume("small")] };
        api.Fail("RemoveVolume", new EngineException(0, "The engine did not answer within 90 seconds.") { TimedOut = true });
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        await AllWantedAsync(h);

        h.Session.Dispatch(new RemoveRequested(ResourceKind.Volumes, [new RemovalItem("big"), new RemovalItem("small")]));
        var state = await h.WaitFor(s => s.Volumes.Removal is { IsRunning: false });

        Assert.Equal(ConnectionStatus.Connected, state.Status);
        Assert.Equal(2, state.Volumes.Removal!.Refusals.Length);
        Assert.Equal("The engine did not answer within 90 seconds.", state.Volumes.Removal.Refusals[0].Message);
        Assert.False(state.Volumes.Removal.Interrupted);
    }

    private static async Task AllWantedAsync(Harness h)
    {
        h.Session.Dispatch(new ImagesWanted());
        h.Session.Dispatch(new VolumesWanted());
        h.Session.Dispatch(new NetworksWanted());
        await h.WaitFor(s => s.Images.Loaded && s.Volumes.Loaded && s.Networks.Loaded);
    }

    [Fact]
    public async Task A_refused_remove_shows_the_engine_message_and_keeps_the_row()
    {
        var api = new FakeEngineApi { Volumes = [Volume("data")] };
        api.Fail("RemoveVolume", new EngineException(409, "remove data: volume is in use - [c1]"));
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        h.Session.Dispatch(new VolumesWanted());
        await h.WaitFor(s => s.Volumes.Loaded);

        h.Session.Dispatch(new RemoveRequested(ResourceKind.Volumes, [new RemovalItem("data")]));
        var state = await h.WaitFor(s => s.Volumes.Removal is { IsRunning: false });

        Assert.Equal<RemovalRefusal>([new RemovalRefusal("data", "remove data: volume is in use - [c1]")], state.Volumes.Removal!.Refusals);
        Assert.Equal(["data"], state.Volumes.Items.Select(v => v.Name));
        Assert.Empty(state.Volumes.Pending);
        Assert.Equal(ConnectionStatus.Connected, state.Status);
        var (what, _) = await h.WaitForError();
        Assert.Equal("a volume action failed", what);
    }

    [Fact]
    public async Task A_pulls_progress_reaches_the_state_in_batches_of_at_most_ten_a_second()
    {
        var api = new FakeEngineApi();
        var stream = api.PullOf("nginx:latest");
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        h.Session.Dispatch(new ImagesWanted());
        await h.WaitFor(s => s.Images.Loaded);

        h.Session.Dispatch(new PullRequested("nginx"));
        await stream.WhenOpened().WaitAsync(Patience);
        var changes = 0;
        h.Session.StateChanged += _ => Interlocked.Increment(ref changes);

        var recorded = h.WaitForPullLines(3);
        stream.Push(Line("l1", 10));
        stream.Push(Line("l2", 20));
        stream.Push(Line("l1", 30));
        await recorded;
        Assert.Equal(0, Volatile.Read(ref changes));

        h.Time.Advance(EngineSession.PullBatchInterval);
        var state = await h.WaitFor(s => s.Pulls["nginx:latest"].Layers.Length == 2);
        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Equal(30, state.Pulls["nginx:latest"].Layers[0].Current);

        recorded = h.WaitForPullLines(1);
        stream.Push(Line("l2", 90));
        await recorded;
        stream.End();
        var done = await h.WaitFor(s => s.Pulls["nginx:latest"].Status == PullStatus.Done);

        // The lines read after the last batch arrive before the pull is done.
        Assert.Equal(90, done.Pulls["nginx:latest"].Layers[1].Current);
        Assert.Contains("Pull nginx:latest", api.Calls);
    }

    [Fact]
    public async Task A_pull_the_engine_refuses_keeps_its_message_and_can_run_again()
    {
        var api = new FakeEngineApi();
        var stream = api.PullOf("nope:latest");
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        h.Session.Dispatch(new ImagesWanted());
        await h.WaitFor(s => s.Images.Loaded);

        h.Session.Dispatch(new PullRequested("nope"));
        await stream.WhenOpened().WaitAsync(Patience);
        stream.End(new EngineException(200, "pull access denied for nope, repository does not exist"));
        var failed = await h.WaitFor(s => s.Pulls.TryGetValue("nope:latest", out var p) && p.Status == PullStatus.Failed);

        Assert.Equal("pull access denied for nope, repository does not exist", failed.Pulls["nope:latest"].Error);
        Assert.Equal(ConnectionStatus.Connected, failed.Status);

        h.Session.Dispatch(new PullRequested("nope:latest"));
        await stream.WhenOpened(2).WaitAsync(Patience);
        stream.End();
        await h.WaitFor(s => s.Pulls["nope:latest"].Status == PullStatus.Done);
    }

    [Fact]
    public async Task A_cancelled_pull_stops_its_stream_and_reports_nothing()
    {
        var api = new FakeEngineApi();
        var stream = api.PullOf("nginx:latest");
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        h.Session.Dispatch(new PullRequested("nginx"));
        await stream.WhenOpened().WaitAsync(Patience);

        h.Session.Dispatch(new PullCancelRequested("nginx:latest"));
        await api.WhenCancelled("Pull nginx:latest").WaitAsync(Patience);
        var state = await h.WaitFor(s => s.Pulls.IsEmpty);

        Assert.Empty(h.Errors);
        Assert.Equal(ConnectionStatus.Connected, state.Status);
    }

    [Fact]
    public async Task Two_pulls_run_apart()
    {
        var api = new FakeEngineApi();
        var nginx = api.PullOf("nginx:latest");
        var alpine = api.PullOf("alpine:latest");
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        h.Session.Dispatch(new ImagesWanted());
        await h.WaitFor(s => s.Images.Loaded);

        h.Session.Dispatch(new PullRequested("nginx"));
        h.Session.Dispatch(new PullRequested("alpine"));
        await nginx.WhenOpened().WaitAsync(Patience);
        await alpine.WhenOpened().WaitAsync(Patience);
        alpine.End(new EngineException(200, "manifest unknown"));
        nginx.End();

        var state = await h.WaitFor(s => s.Pulls.Values.All(p => p.Status != PullStatus.Running));
        Assert.Equal(PullStatus.Done, state.Pulls["nginx:latest"].Status);
        Assert.Equal("manifest unknown", state.Pulls["alpine:latest"].Error);
    }

    [Fact]
    public async Task Running_a_container_creates_then_starts_it()
    {
        var api = new FakeEngineApi { CreatedId = "c9" };
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        var request = new ContainerCreateRequest("alpine", "berth-it-x", ["A=1"], [], []);

        h.Session.Dispatch(new RunContainerRequested(request));
        var state = await h.WaitFor(s => s.Run?.Status == RunStatus.Started);

        Assert.Equal("c9", state.Run!.ContainerId);
        var calls = api.Calls.ToList();
        Assert.True(calls.IndexOf("CreateContainer alpine name=berth-it-x") < calls.IndexOf("Start c9"));
        Assert.Same(request, Assert.Single(api.CreateRequests));
    }

    [Fact]
    public async Task A_start_that_fails_after_the_create_leaves_the_container_and_says_so()
    {
        var api = new FakeEngineApi { CreatedId = "c9" };
        api.Fail("Start", new EngineException(500, "driver failed programming external connectivity: port is already allocated"));
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        h.Session.Dispatch(new RunContainerRequested(new ContainerCreateRequest("alpine", null, [], [], [])));
        var state = await h.WaitFor(s => s.Run?.Status == RunStatus.StartFailed);

        Assert.Equal("c9", state.Run!.ContainerId);
        Assert.Equal("driver failed programming external connectivity: port is already allocated", state.Run.Error);
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("Remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_create_shows_the_engine_message_and_starts_nothing()
    {
        var api = new FakeEngineApi();
        api.Fail("CreateContainer", new EngineException(404, "No such image: nope:latest"));
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        h.Session.Dispatch(new RunContainerRequested(new ContainerCreateRequest("nope", null, [], [], [])));
        var state = await h.WaitFor(s => s.Run?.Status == RunStatus.Failed);

        Assert.Equal("No such image: nope:latest", state.Run!.Error);
        Assert.Null(state.Run.ContainerId);
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("Start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_list_read_on_a_connection_that_was_replaced_is_dropped()
    {
        var first = new FakeEngineApi { IgnoreCancellation = true, Images = [Image("old")] };
        var held = first.Hold("ListImages");
        var second = new FakeEngineApi { Images = [Image("new")] };
        await using var h = Harness.With(first, second);
        try
        {
            await h.ConnectAsync();
            h.Session.Dispatch(new ImagesWanted());
            await held.Entered.WaitAsync(Patience);

            h.Session.Dispatch(new ConnectRequested());
            await h.WaitFor(s => s.Images.Loaded && h.Created.Count == 2);
            var dropped = h.WaitForDropped<ImagesLoaded>();
            held.Release();
            await dropped;

            Assert.Equal(["new"], h.Session.State.Images.Items.Select(i => i.Id));
        }
        finally
        {
            // The old connection is retired only once its calls end, and this one ignores cancelling.
            held.Release();
            first.EndStream();
        }
    }
}

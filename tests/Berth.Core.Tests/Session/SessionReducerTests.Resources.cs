using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Session;
using Berth.State;

namespace Berth.Tests.Session;

/// <summary>The images, volumes and networks lists, their actions, disk use, pulls and running a container.</summary>
public sealed partial class SessionReducerTests
{
    private static readonly DiskUsage Usage = new([], new UsageTotals(1, 0, 100, 100), [], new UsageTotals(0, 0, 0, 0));

    private static ImageSummary Image(string id, params string[] tags) => new(id, tags, [], 1_700_000_000, 1000, 0);

    private static VolumeSummary Volume(string name) => new(name, "local", $"/v/{name}", null, new Dictionary<string, string>(), "local");

    private static NetworkSummary Network(string id) => new(id, id, "bridge", "local", null, false, new Dictionary<string, string>(), []);

    private static ContainerCreateRequest RunRequest => new("alpine", "berth-it-x", [], [], []);

    /// <summary>Connected, with the images list wanted and loaded: sha256:a tagged nginx:latest and nginx:1, and sha256:b untagged.</summary>
    private static EngineState WithImages()
    {
        var (wanted, effects) = Reduce(Connected(), new ImagesWanted());
        var load = Assert.IsType<LoadResourceEffect>(Assert.Single(effects));
        var (loaded, _) = Reduce(wanted, new ImagesLoaded(load.Generation, [Image("sha256:a", "nginx:latest", "nginx:1"), Image("sha256:b")]));
        return loaded;
    }

    private static EngineState WithVolumes()
    {
        var (wanted, effects) = Reduce(Connected(), new VolumesWanted());
        var load = Assert.IsType<LoadResourceEffect>(Assert.Single(effects));
        return Reduce(wanted, new VolumesLoaded(load.Generation, [Volume("data"), Volume("logs")])).State;
    }

    private static EngineState WithNetworks()
    {
        var (wanted, effects) = Reduce(Connected(), new NetworksWanted());
        var load = Assert.IsType<LoadResourceEffect>(Assert.Single(effects));
        return Reduce(wanted, new NetworksLoaded(load.Generation, [Network("n1"), Network("bridge")])).State;
    }

    private static EngineState Pulling(EngineState state, string reference)
    {
        var (next, _) = Reduce(state, new PullRequested(reference));
        return next;
    }

    // Lists

    [Fact]
    public void Images_wanted_reads_the_list_with_a_new_generation()
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected, new ImagesWanted());

        Assert.True(state.Images.Status.Wanted);
        Assert.False(state.Images.Loaded);
        Assert.Equal(connected.Ticket + 1, state.Images.Status.Generation);
        Assert.Equal([new LoadResourceEffect(ResourceKind.Images, state.Images.Status.Generation)], effects);
    }

    [Theory]
    [InlineData(ResourceKind.Volumes)]
    [InlineData(ResourceKind.Networks)]
    public void Volumes_and_networks_wanted_read_their_list(ResourceKind kind)
    {
        SessionEvent wanted = kind == ResourceKind.Volumes ? new VolumesWanted() : new NetworksWanted();

        var (state, effects) = Reduce(Connected(), wanted);

        var status = kind == ResourceKind.Volumes ? state.Volumes.Status : state.Networks.Status;
        Assert.True(status.Wanted);
        Assert.Equal([new LoadResourceEffect(kind, status.Generation)], effects);
    }

    [Fact]
    public void A_list_is_read_only_the_first_time_its_page_is_shown()
    {
        var loaded = WithImages();

        var (state, effects) = Reduce(loaded, new ImagesWanted());

        Assert.Same(loaded, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_list_wanted_while_disconnected_is_read_once_connected()
    {
        var (wanted, effects) = Reduce(EngineState.Initial, new ImagesWanted());
        Assert.True(wanted.Images.Status.Wanted);
        Assert.Empty(effects);

        var (connecting, _) = Reduce(wanted, new ConnectRequested());
        var (connected, connectEffects) = Reduce(connecting, new ConnectSucceeded(Version, Info));

        Assert.Contains(new LoadResourceEffect(ResourceKind.Images, connected.Images.Status.Generation), connectEffects);
        Assert.DoesNotContain(connectEffects, e => e is LoadResourceEffect { Kind: not ResourceKind.Images });
    }

    [Fact]
    public void A_loaded_list_replaces_the_items_and_an_older_load_is_ignored()
    {
        var (wanted, effects) = Reduce(Connected(), new ImagesWanted());
        var first = Assert.IsType<LoadResourceEffect>(Assert.Single(effects));
        var (reloading, _) = Reduce(wanted, new ReloadRequested());

        var (stale, _) = Reduce(reloading, new ImagesLoaded(first.Generation, [Image("old")]));
        Assert.Same(reloading, stale);

        var (state, _) = Reduce(reloading, new ImagesLoaded(reloading.Images.Status.Generation, [Image("new", "x:1")]));
        Assert.True(state.Images.Loaded);
        Assert.Equal(["new"], state.Images.Items.Select(i => i.Id));
    }

    [Fact]
    public void Volumes_and_networks_loaded_replace_their_items()
    {
        Assert.Equal(["data", "logs"], WithVolumes().Volumes.Items.Select(v => v.Name));
        Assert.True(WithVolumes().Volumes.Loaded);
        Assert.Equal(["n1", "bridge"], WithNetworks().Networks.Items.Select(n => n.Id));
    }

    [Fact]
    public void A_failed_list_load_shows_the_engine_message_and_keeps_the_items()
    {
        var loaded = WithImages();
        var (reloading, effects) = Reduce(loaded, new ReloadRequested());
        var load = effects.OfType<LoadResourceEffect>().Single();

        var (state, _) = Reduce(reloading, new ResourceLoadFailed(ResourceKind.Images, load.Generation, "images broke", EngineUnreachable: false));

        Assert.Equal("images broke", state.ActionError);
        Assert.Equal(loaded.Images.Items, state.Images.Items);
        Assert.Equal(ConnectionStatus.Connected, state.Status);
    }

    [Fact]
    public void A_list_load_that_cannot_reach_the_engine_moves_to_disconnected()
    {
        var (wanted, effects) = Reduce(Connected(), new VolumesWanted());
        var load = Assert.IsType<LoadResourceEffect>(Assert.Single(effects));

        var (state, _) = Reduce(wanted, new ResourceLoadFailed(ResourceKind.Volumes, load.Generation, "connection reset", EngineUnreachable: true));

        Assert.Equal(ConnectionStatus.Disconnected, state.Status);
        Assert.Equal("connection reset", state.ConnectionError);
    }

    [Theory]
    [InlineData("image", "pull", ResourceKind.Images)]
    [InlineData("image", "tag", ResourceKind.Images)]
    [InlineData("image", "untag", ResourceKind.Images)]
    [InlineData("image", "delete", ResourceKind.Images)]
    [InlineData("image", "import", ResourceKind.Images)]
    [InlineData("image", "load", ResourceKind.Images)]
    [InlineData("volume", "create", ResourceKind.Volumes)]
    [InlineData("volume", "destroy", ResourceKind.Volumes)]
    [InlineData("volume", "mount", ResourceKind.Volumes)]
    [InlineData("volume", "unmount", ResourceKind.Volumes)]
    [InlineData("network", "create", ResourceKind.Networks)]
    [InlineData("network", "destroy", ResourceKind.Networks)]
    [InlineData("network", "connect", ResourceKind.Networks)]
    [InlineData("network", "disconnect", ResourceKind.Networks)]
    public void An_engine_event_schedules_a_debounced_reload_of_its_list(string type, string action, ResourceKind kind)
    {
        var state = AllListsLoaded();

        var (next, effects) = Reduce(state, new EngineEventReceived(Event(type, action)));

        Assert.Contains(new ScheduleResourceReloadEffect(kind, SessionReducer.ReloadDebounce), effects);
        Assert.DoesNotContain(effects, e => e is ScheduleResourceReloadEffect r && r.Kind != kind);
        Assert.True(StatusOf(next, kind).ReloadScheduled);

        var (again, againEffects) = Reduce(next, new EngineEventReceived(Event(type, action)));
        Assert.Same(next, again);
        Assert.Empty(againEffects);
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("disconnect")]
    public void A_container_joining_or_leaving_a_network_reloads_the_containers_which_say_what_is_in_use(string action)
    {
        var state = AllListsLoaded();

        var (next, effects) = Reduce(state, new EngineEventReceived(Event("network", action)));

        Assert.Equal(
            [
                new ScheduleReloadEffect(SessionReducer.ReloadDebounce),
                new ScheduleResourceReloadEffect(ResourceKind.Networks, SessionReducer.ReloadDebounce),
            ],
            effects);
        Assert.True(next.ReloadScheduled);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("destroy")]
    public void A_container_created_or_destroyed_reloads_images_and_volumes_too(string action)
    {
        var state = AllListsLoaded();

        var (next, effects) = Reduce(state, new EngineEventReceived(Event("container", action)));

        Assert.Equal(
            [
                new ScheduleReloadEffect(SessionReducer.ReloadDebounce),
                new ScheduleResourceReloadEffect(ResourceKind.Images, SessionReducer.ReloadDebounce),
                new ScheduleResourceReloadEffect(ResourceKind.Volumes, SessionReducer.ReloadDebounce),
            ],
            effects);
        Assert.False(next.Networks.Status.ReloadScheduled);
    }

    [Theory]
    [InlineData("image", "pull")]
    [InlineData("volume", "create")]
    [InlineData("network", "create")]
    [InlineData("container", "destroy")]
    public void An_engine_event_does_not_read_a_list_whose_page_was_never_shown(string type, string action)
    {
        var connected = Connected();

        var (_, effects) = Reduce(connected, new EngineEventReceived(Event(type, action)));

        Assert.DoesNotContain(effects, e => e is ScheduleResourceReloadEffect or LoadResourceEffect);
    }

    [Theory]
    [InlineData("image", "inspect")]
    [InlineData("volume", "prune")]
    [InlineData("network", "prune")]
    public void Other_engine_events_do_not_reload_a_list(string type, string action)
    {
        var state = AllListsLoaded();

        var (next, effects) = Reduce(state, new EngineEventReceived(Event(type, action)));

        Assert.Same(state, next);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_reload_due_reads_the_list_again_and_while_disconnected_only_clears_the_flag()
    {
        var (scheduled, _) = Reduce(WithImages(), new EngineEventReceived(Event("image", "tag")));

        var (state, effects) = Reduce(scheduled, new ResourceReloadDue(ResourceKind.Images));

        Assert.False(state.Images.Status.ReloadScheduled);
        Assert.Equal([new LoadResourceEffect(ResourceKind.Images, state.Images.Status.Generation)], effects);
        Assert.True(state.Images.Status.Generation > scheduled.Images.Status.Generation);

        var disconnected = scheduled with { Status = ConnectionStatus.Disconnected };
        var (cleared, none) = Reduce(disconnected, new ResourceReloadDue(ResourceKind.Images));
        Assert.False(cleared.Images.Status.ReloadScheduled);
        Assert.Empty(none);
    }

    [Fact]
    public void Reload_requested_reads_every_wanted_list_too()
    {
        var state = WithVolumes();

        var (next, effects) = Reduce(state, new ReloadRequested());

        Assert.Equal(
            [new LoadContainersEffect(next.LoadGeneration), new LoadResourceEffect(ResourceKind.Volumes, next.Volumes.Status.Generation)],
            effects);
    }

    [Fact]
    public void Losing_the_connection_keeps_the_lists_and_forgets_what_was_under_way()
    {
        var (removing, _) = Reduce(WithImages(), new RemoveRequested(ResourceKind.Images, [new RemovalItem("nginx:1")]));
        var (scheduled, _) = Reduce(removing, new EngineEventReceived(Event("image", "tag")));

        var (state, _) = Reduce(scheduled, new ConnectionLost("connection reset"));

        Assert.Equal(scheduled.Images.Items, state.Images.Items);
        Assert.Empty(state.Images.Pending);
        Assert.False(state.Images.Status.ReloadScheduled);
        Assert.True(state.Images.Status.Wanted);
    }

    [Fact]
    public void Connecting_again_marks_the_lists_not_loaded_until_they_are_read_again()
    {
        var loaded = WithImages();

        var (connecting, _) = Reduce(loaded, new ConnectRequested());
        Assert.False(connecting.Images.Loaded);
        Assert.Equal(loaded.Images.Items, connecting.Images.Items);

        var (connected, effects) = Reduce(connecting, new ConnectSucceeded(Version, Info));
        Assert.Contains(new LoadResourceEffect(ResourceKind.Images, connected.Images.Status.Generation), effects);
    }

    // Removes ("Delete" and "Remove unused …" alike), and create

    private static RemoveRequested RemoveVolumes(params string[] names) => new(ResourceKind.Volumes, [.. names.Select(n => new RemovalItem(n))]);

    private static EngineState WithManyVolumes(int count)
    {
        var (wanted, effects) = Reduce(Connected(), new VolumesWanted());
        var load = Assert.IsType<LoadResourceEffect>(Assert.Single(effects));
        return Reduce(wanted, new VolumesLoaded(load.Generation, [.. Enumerable.Range(1, count).Select(i => Volume($"v{i}"))])).State;
    }

    [Fact]
    public void A_remove_marks_every_key_pending_and_sends_at_most_four_items_at_once()
    {
        var state = WithManyVolumes(6);

        var (next, effects) = Reduce(state, RemoveVolumes("v1", "v2", "v3", "v4", "v5", "v6"));

        Assert.Equal(
            [.. new[] { "v1", "v2", "v3", "v4" }.Select(n => (SessionEffect)new RemoveItemEffect(ResourceKind.Volumes, new RemovalItem(n)))],
            effects);
        Assert.Equal(6, next.Volumes.Pending.Count);
        Assert.Equal((6, 0, 4), (next.Volumes.Removal!.Total, next.Volumes.Removal.Finished, next.Volumes.Removal.Running));
        Assert.Equal<RemovalItem>([new RemovalItem("v5"), new RemovalItem("v6")], next.Volumes.Removal!.Queued);
        Assert.Equal(4, SessionReducer.RemovalConcurrency);
    }

    [Fact]
    public void An_image_is_one_item_whose_tags_go_in_order_in_one_effect()
    {
        var (next, effects) = Reduce(WithImages(), new RemoveRequested(ResourceKind.Images, [new RemovalItem("nginx:latest", "nginx:1"), new RemovalItem("sha256:b")]));

        Assert.Equal(
            [
                new RemoveItemEffect(ResourceKind.Images, new RemovalItem("nginx:latest", "nginx:1")),
                new RemoveItemEffect(ResourceKind.Images, new RemovalItem("sha256:b")),
            ],
            effects);
        Assert.Equal(["nginx:1", "nginx:latest", "sha256:b"], next.Images.Pending.Keys.Order());
    }

    [Fact]
    public void An_item_with_a_key_already_pending_is_left_out()
    {
        var (removing, _) = Reduce(WithImages(), new RemoveRequested(ResourceKind.Images, [new RemovalItem("nginx:1")]));

        var (again, effects) = Reduce(removing, new RemoveRequested(ResourceKind.Images, [new RemovalItem("nginx:latest", "nginx:1")]));

        Assert.Same(removing, again);
        Assert.Empty(effects);
    }

    [Fact]
    public void An_item_that_ends_frees_its_keys_sends_the_next_one_and_keeps_a_refusal()
    {
        var (removing, _) = Reduce(WithManyVolumes(6), RemoveVolumes("v1", "v2", "v3", "v4", "v5", "v6"));

        var (one, oneEffects) = Reduce(removing, new RemoveItemFinished(ResourceKind.Volumes, new RemovalItem("v1"), null));
        Assert.Equal([new RemoveItemEffect(ResourceKind.Volumes, new RemovalItem("v5"))], oneEffects);
        Assert.False(one.Volumes.Pending.ContainsKey("v1"));
        Assert.Equal((1, 4), (one.Volumes.Removal!.Finished, one.Volumes.Removal.Running));

        var refusal = new RemovalRefusal("v2", "remove v2: volume is in use - [c1]");
        var (two, _) = Reduce(one, new RemoveItemFinished(ResourceKind.Volumes, new RemovalItem("v2"), refusal));
        Assert.Equal<RemovalRefusal>([refusal], two.Volumes.Removal!.Refusals);
        Assert.False(two.Volumes.Pending.ContainsKey("v2"));
        Assert.Null(two.ActionError);
        Assert.True(two.Volumes.Removal.IsRunning);
    }

    [Theory]
    [InlineData(ResourceKind.Images, "sha256:a", "conflict: unable to delete sha256:a (cannot be forced) - image is being used by running container c1")]
    [InlineData(ResourceKind.Volumes, "data", "remove data: volume is in use - [c1]")]
    [InlineData(ResourceKind.Networks, "bridge", "bridge is a pre-defined network and cannot be removed")]
    public void A_refused_remove_shows_the_engine_message_as_is_and_the_row_stays(ResourceKind kind, string key, string message)
    {
        var loaded = AllListsLoaded();
        var (removing, _) = Reduce(loaded, new RemoveRequested(kind, [new RemovalItem(key)]));

        var (state, effects) = Reduce(removing, new RemoveItemFinished(kind, new RemovalItem(key), new RemovalRefusal(key, message)));

        Assert.Equal<RemovalRefusal>([new RemovalRefusal(key, message)], StatusOf(state, kind).Removal!.Refusals);
        Assert.Empty(StatusOf(state, kind).Pending);
        Assert.Equal(loaded.Images.Items, state.Images.Items);
        Assert.Equal(loaded.Volumes.Items, state.Volumes.Items);
        Assert.Equal(loaded.Networks.Items, state.Networks.Items);
        Assert.Equal([new ScheduleResourceReloadEffect(kind, SessionReducer.ReloadDebounce)], effects);
    }

    [Fact]
    public void A_batch_reads_the_list_and_the_asked_disk_use_once_when_its_last_item_ends()
    {
        var (asked, askEffects) = Reduce(WithManyVolumes(3), new DiskUsageRequested());
        var (withUsage, _) = Reduce(asked, new DiskUsageLoaded(Assert.IsType<LoadDiskUsageEffect>(Assert.Single(askEffects)).Generation, Usage));
        var (removing, _) = Reduce(withUsage, RemoveVolumes("v1", "v2", "v3"));

        var (one, oneEffects) = Reduce(removing, new RemoveItemFinished(ResourceKind.Volumes, new RemovalItem("v1"), null));
        var (two, twoEffects) = Reduce(one, new RemoveItemFinished(ResourceKind.Volumes, new RemovalItem("v2"), null));
        var (state, lastEffects) = Reduce(two, new RemoveItemFinished(ResourceKind.Volumes, new RemovalItem("v3"), null));

        Assert.Empty(oneEffects);
        Assert.Empty(twoEffects);
        Assert.Equal(
            [new ScheduleResourceReloadEffect(ResourceKind.Volumes, SessionReducer.ReloadDebounce), new LoadDiskUsageEffect(state.DiskUsage.Generation)],
            lastEffects);
        Assert.False(state.Volumes.Removal!.IsRunning);
        Assert.Equal(3, state.Volumes.Removal.Finished);
    }

    [Fact]
    public void A_batch_never_reads_the_disk_use_nobody_asked_for_nor_for_networks()
    {
        var (removing, _) = Reduce(WithImages(), new RemoveRequested(ResourceKind.Images, [new RemovalItem("sha256:b")]));
        var (_, effects) = Reduce(removing, new RemoveItemFinished(ResourceKind.Images, new RemovalItem("sha256:b"), null));
        Assert.Equal([new ScheduleResourceReloadEffect(ResourceKind.Images, SessionReducer.ReloadDebounce)], effects);

        var (asked, _) = Reduce(WithNetworks(), new DiskUsageRequested());
        var (removingNetwork, _) = Reduce(asked, new RemoveRequested(ResourceKind.Networks, [new RemovalItem("n1")]));
        var (_, networkEffects) = Reduce(removingNetwork, new RemoveItemFinished(ResourceKind.Networks, new RemovalItem("n1"), null));
        Assert.DoesNotContain(networkEffects, e => e is LoadDiskUsageEffect);
    }

    [Fact]
    public void A_remove_asked_for_while_a_batch_runs_joins_it()
    {
        var (removing, _) = Reduce(WithManyVolumes(6), RemoveVolumes("v1", "v2", "v3", "v4", "v5"));

        var (joined, effects) = Reduce(removing, RemoveVolumes("v6"));

        Assert.Empty(effects);
        Assert.Equal(6, joined.Volumes.Removal!.Total);
        Assert.Equal(2, joined.Volumes.Removal.Queued.Length);
    }

    [Fact]
    public void An_ended_batch_stays_until_dismissed_and_the_next_remove_starts_a_new_one()
    {
        var (removing, _) = Reduce(WithManyVolumes(2), RemoveVolumes("v1"));
        Assert.Same(removing, Reduce(removing, new RemovalDismissed(ResourceKind.Volumes)).State);
        var (ended, _) = Reduce(removing, new RemoveItemFinished(ResourceKind.Volumes, new RemovalItem("v1"), new RemovalRefusal("v1", "in use")));

        var (dismissed, _) = Reduce(ended, new RemovalDismissed(ResourceKind.Volumes));
        Assert.Null(dismissed.Volumes.Removal);

        var (fresh, _) = Reduce(ended, RemoveVolumes("v2"));
        Assert.Equal((1, 0, 1), (fresh.Volumes.Removal!.Total, fresh.Volumes.Removal.Finished, fresh.Volumes.Removal.Running));
        Assert.Empty(fresh.Volumes.Removal.Refusals);
    }

    [Fact]
    public void A_remove_that_cannot_reach_the_engine_moves_to_disconnected_and_ends_the_batch()
    {
        var (removing, _) = Reduce(WithManyVolumes(6), RemoveVolumes("v1", "v2", "v3", "v4", "v5", "v6"));

        var (state, _) = Reduce(removing, new RemoveItemFinished(ResourceKind.Volumes, new RemovalItem("v1"), new RemovalRefusal("v1", "connection reset"), EngineUnreachable: true));

        Assert.Equal(ConnectionStatus.Disconnected, state.Status);
        Assert.Equal("connection reset", state.ConnectionError);
        Assert.Empty(state.Volumes.Pending);
        Assert.False(state.Volumes.Removal!.IsRunning);
        Assert.Empty(state.Volumes.Removal.Queued);

        var (late, _) = Reduce(state, new RemoveItemFinished(ResourceKind.Volumes, new RemovalItem("v2"), null));
        Assert.Same(state, late);
    }

    [Fact]
    public void A_pending_remove_of_an_item_no_longer_listed_is_forgotten_on_reload()
    {
        var (removing, _) = Reduce(WithImages(), new RemoveRequested(ResourceKind.Images, [new RemovalItem("nginx:1"), new RemovalItem("sha256:b")]));
        var (reloading, effects) = Reduce(removing, new ReloadRequested());
        var load = effects.OfType<LoadResourceEffect>().Single();

        var (state, _) = Reduce(reloading, new ImagesLoaded(load.Generation, [Image("sha256:a", "nginx:latest", "nginx:1")]));

        Assert.Equal(["nginx:1"], state.Images.Pending.Keys);
    }

    [Fact]
    public void Actions_are_ignored_while_not_connected()
    {
        var disconnected = WithImages() with { Status = ConnectionStatus.Disconnected };

        Assert.Same(disconnected, Reduce(disconnected, new RemoveRequested(ResourceKind.Images, [new RemovalItem("sha256:b")])).State);
        Assert.Same(disconnected, Reduce(disconnected, new CreateVolumeRequested("v")).State);
        Assert.Same(disconnected, Reduce(disconnected, new PullRequested("nginx")).State);
        Assert.Same(disconnected, Reduce(disconnected, new RunContainerRequested(RunRequest)).State);
        Assert.Same(disconnected, Reduce(disconnected, new DiskUsageRequested()).State);
    }

    [Fact]
    public void Create_volume_is_pending_under_its_name_and_reloads_the_volumes_when_done()
    {
        var (creating, effects) = Reduce(WithVolumes(), new CreateVolumeRequested("berth-check-vol"));

        Assert.Equal(ItemAction.Create, creating.Volumes.Pending["berth-check-vol"]);
        Assert.Equal([new CreateVolumeEffect("berth-check-vol")], effects);

        var (state, doneEffects) = Reduce(creating, new VolumeCreated("berth-check-vol"));
        Assert.Empty(state.Volumes.Pending);
        Assert.Equal([new ScheduleResourceReloadEffect(ResourceKind.Volumes, SessionReducer.ReloadDebounce)], doneEffects);
    }

    [Fact]
    public void A_refused_volume_create_shows_the_engine_message_as_is()
    {
        var (creating, _) = Reduce(WithVolumes(), new CreateVolumeRequested("data"));

        var (state, _) = Reduce(creating, new VolumeCreateFailed("data", "volume name is already in use", EngineUnreachable: false));

        Assert.Equal("volume name is already in use", state.ActionError);
        Assert.Empty(state.Volumes.Pending);
    }

    // Disk use

    [Fact]
    public void Disk_use_is_not_read_until_asked_for()
    {
        var state = AllListsLoaded();

        Assert.False(state.DiskUsage.Asked);
        Assert.Null(state.DiskUsage.Usage);
    }

    [Fact]
    public void Disk_use_asked_for_is_read_and_an_older_read_is_ignored()
    {
        var connected = Connected();

        var (loading, effects) = Reduce(connected, new DiskUsageRequested());
        Assert.True(loading.DiskUsage.Asked);
        Assert.True(loading.DiskUsage.Loading);
        Assert.Equal([new LoadDiskUsageEffect(loading.DiskUsage.Generation)], effects);

        var (stale, _) = Reduce(loading, new DiskUsageLoaded(loading.DiskUsage.Generation - 1, Usage));
        Assert.Same(loading, stale);

        var (state, _) = Reduce(loading, new DiskUsageLoaded(loading.DiskUsage.Generation, Usage));
        Assert.False(state.DiskUsage.Loading);
        Assert.Same(Usage, state.DiskUsage.Usage);
    }

    [Fact]
    public void Disk_use_asked_for_while_it_is_being_read_is_read_once_more_when_that_read_ends()
    {
        var (loading, _) = Reduce(Connected(), new DiskUsageRequested());

        var (asked, effects) = Reduce(loading, new DiskUsageRequested());
        var (askedAgain, moreEffects) = Reduce(asked, new DiskUsageRequested());
        Assert.Empty(effects);
        Assert.Empty(moreEffects);
        Assert.True(askedAgain.DiskUsage.Again);

        var (rereading, rereadEffects) = Reduce(askedAgain, new DiskUsageLoaded(loading.DiskUsage.Generation, Usage));
        var reread = Assert.IsType<LoadDiskUsageEffect>(Assert.Single(rereadEffects));
        Assert.True(rereading.DiskUsage.Loading);
        Assert.False(rereading.DiskUsage.Again);
        Assert.Same(Usage, rereading.DiskUsage.Usage);

        var (state, lastEffects) = Reduce(rereading, new DiskUsageLoaded(reread.Generation, Usage));
        Assert.False(state.DiskUsage.Loading);
        Assert.Empty(lastEffects);
    }

    [Fact]
    public void A_disk_use_read_asked_for_again_starts_once_more_after_a_failed_read_too()
    {
        var (loading, _) = Reduce(Connected(), new DiskUsageRequested());
        var (asked, _) = Reduce(loading, new DiskUsageRequested());

        var (state, effects) = Reduce(asked, new DiskUsageFailed(loading.DiskUsage.Generation, "df broke", EngineUnreachable: false));

        Assert.Single(effects.OfType<LoadDiskUsageEffect>());
        Assert.True(state.DiskUsage.Loading);
    }

    [Fact]
    public void A_failed_disk_use_read_shows_the_engine_message_and_keeps_the_last_figures()
    {
        var (loading, _) = Reduce(Connected(), new DiskUsageRequested());
        var (loaded, _) = Reduce(loading, new DiskUsageLoaded(loading.DiskUsage.Generation, Usage));
        var (again, _) = Reduce(loaded, new DiskUsageRequested());

        var (state, _) = Reduce(again, new DiskUsageFailed(again.DiskUsage.Generation, "df broke", EngineUnreachable: false));

        Assert.False(state.DiskUsage.Loading);
        Assert.Same(Usage, state.DiskUsage.Usage);
        Assert.Equal("df broke", state.ActionError);
    }

    [Fact]
    public void Disk_use_once_asked_for_is_read_again_on_reconnect()
    {
        var (loading, _) = Reduce(Connected(), new DiskUsageRequested());

        var (asked, _) = Reduce(loading, new DiskUsageRequested());
        var (lost, _) = Reduce(asked, new ConnectionLost("connection reset"));
        Assert.False(lost.DiskUsage.Loading);
        Assert.False(lost.DiskUsage.Again);

        var (retrying, _) = Reduce(lost, new RetryDue());
        var (state, effects) = Reduce(retrying, new ConnectSucceeded(Version, Info));
        Assert.Contains(new LoadDiskUsageEffect(state.DiskUsage.Generation), effects);
    }

    // Pulls

    [Fact]
    public void A_pull_runs_under_its_reference_as_split_for_the_engine()
    {
        var state = WithImages();

        var (next, effects) = Reduce(state, new PullRequested(" nginx "));

        var pull = next.Pulls["nginx:latest"];
        Assert.Equal(PullStatus.Running, pull.Status);
        Assert.Equal("nginx:latest", pull.Reference);
        Assert.Equal([new StartPullEffect("nginx:latest", pull.Generation)], effects);
    }

    [Theory]
    [InlineData("nginx@sha256:abc", "nginx@sha256:abc")]
    [InlineData("host:5000/a/b", "host:5000/a/b:latest")]
    [InlineData("alpine:3.19", "alpine:3.19")]
    public void A_pull_reference_is_normalised(string typed, string key)
    {
        var (next, _) = Reduce(WithImages(), new PullRequested(typed));

        Assert.Equal([key], next.Pulls.Keys);
    }

    [Fact]
    public void A_pull_of_a_reference_already_running_does_nothing()
    {
        var pulling = Pulling(WithImages(), "nginx");

        var (state, effects) = Reduce(pulling, new PullRequested("nginx:latest"));

        Assert.Same(pulling, state);
        Assert.Empty(effects);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("nginx:")]
    [InlineData(":tag")]
    public void A_pull_of_something_that_is_no_reference_does_nothing(string typed)
    {
        var state = WithImages();

        var (next, effects) = Reduce(state, new PullRequested(typed));

        Assert.Same(state, next);
        Assert.Empty(effects);
    }

    [Fact]
    public void Pull_progress_updates_its_layers_and_an_older_attempt_is_ignored()
    {
        var pulling = Pulling(WithImages(), "nginx");
        var generation = pulling.Pulls["nginx:latest"].Generation;

        var (state, _) = Reduce(pulling, new PullProgressArrived("nginx:latest", generation,
        [
            new PullProgress("latest", "Pulling from library/nginx", null, null),
            new PullProgress("l1", "Downloading", 10, 100),
            new PullProgress("l2", "Waiting", null, null),
            new PullProgress("l1", "Downloading", 40, 100),
        ]));

        var pull = state.Pulls["nginx:latest"];
        Assert.Equal<PullLayer>([new PullLayer("l1", "Downloading", 40, 100), new PullLayer("l2", "Waiting", null, null)], pull.Layers);
        Assert.Equal("Pulling from library/nginx", pull.LastStatus);

        var (stale, _) = Reduce(state, new PullProgressArrived("nginx:latest", generation - 1, [new PullProgress("l3", "Waiting", null, null)]));
        Assert.Same(state, stale);
    }

    [Fact]
    public void A_finished_pull_stays_until_a_later_image_load_shows_the_image()
    {
        var pulling = Pulling(WithImages(), "nginx");
        var generation = pulling.Pulls["nginx:latest"].Generation;

        var (done, effects) = Reduce(pulling, new PullFinished("nginx:latest", generation));
        Assert.Equal(PullStatus.Done, done.Pulls["nginx:latest"].Status);
        Assert.Equal([new ScheduleResourceReloadEffect(ResourceKind.Images, SessionReducer.ReloadDebounce)], effects);

        // A load sent before the pull finished does not show the image yet.
        var (stillThere, _) = Reduce(done, new ImagesLoaded(done.Images.Status.Generation, [Image("sha256:a", "nginx:1")]));
        Assert.True(stillThere.Pulls.ContainsKey("nginx:latest"));

        var (reloading, reloadEffects) = Reduce(stillThere, new ResourceReloadDue(ResourceKind.Images));
        var load = Assert.IsType<LoadResourceEffect>(Assert.Single(reloadEffects));
        var (state, _) = Reduce(reloading, new ImagesLoaded(load.Generation, [Image("sha256:a", "nginx:latest", "nginx:1")]));
        Assert.Empty(state.Pulls);
    }

    [Fact]
    public void A_finished_pull_goes_at_once_when_the_image_list_is_not_shown()
    {
        var pulling = Pulling(Connected(), "nginx");

        var (state, effects) = Reduce(pulling, new PullFinished("nginx:latest", pulling.Pulls["nginx:latest"].Generation));

        Assert.Empty(state.Pulls);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_failed_pull_keeps_the_engine_message_leaves_the_list_and_can_start_again()
    {
        var loaded = WithImages();
        var pulling = Pulling(loaded, "nope");
        var first = pulling.Pulls["nope:latest"].Generation;

        var (failed, effects) = Reduce(pulling, new PullFailed("nope:latest", first, "pull access denied for nope, repository does not exist", false));

        var pull = failed.Pulls["nope:latest"];
        Assert.Equal(PullStatus.Failed, pull.Status);
        Assert.Equal("pull access denied for nope, repository does not exist", pull.Error);
        Assert.Equal(loaded.Images, failed.Images);
        Assert.Null(failed.ActionError);
        Assert.Empty(effects);

        var (again, againEffects) = Reduce(failed, new PullRequested("nope"));
        var retry = again.Pulls["nope:latest"];
        Assert.Equal(PullStatus.Running, retry.Status);
        Assert.Null(retry.Error);
        Assert.Empty(retry.Layers);
        Assert.True(retry.Generation > first);
        Assert.Equal([new StartPullEffect("nope:latest", retry.Generation)], againEffects);
    }

    [Fact]
    public void A_failed_pull_stays_until_dismissed()
    {
        var pulling = Pulling(WithImages(), "nope");
        var (failed, _) = Reduce(pulling, new PullFailed("nope:latest", pulling.Pulls["nope:latest"].Generation, "manifest unknown", false));

        var (state, _) = Reduce(failed, new PullDismissed("nope"));

        Assert.Empty(state.Pulls);
    }

    [Fact]
    public void A_running_pull_is_not_dismissed()
    {
        var pulling = Pulling(WithImages(), "nginx");

        var (state, _) = Reduce(pulling, new PullDismissed("nginx:latest"));

        Assert.Same(pulling, state);
    }

    [Fact]
    public void A_cancelled_pull_stops_is_forgotten_leaves_the_list_and_can_start_again()
    {
        var loaded = WithImages();
        var pulling = Pulling(loaded, "nginx");
        var first = pulling.Pulls["nginx:latest"].Generation;

        var (cancelled, effects) = Reduce(pulling, new PullCancelRequested("nginx"));
        Assert.Empty(cancelled.Pulls);
        Assert.Equal(loaded.Images, cancelled.Images);
        Assert.Equal([new CancelPullEffect("nginx:latest")], effects);

        var (late, _) = Reduce(cancelled, new PullFailed("nginx:latest", first, "context canceled", false));
        Assert.Same(cancelled, late);

        var (again, againEffects) = Reduce(cancelled, new PullRequested("nginx"));
        Assert.Equal(PullStatus.Running, again.Pulls["nginx:latest"].Status);
        Assert.Single(againEffects);
    }

    [Fact]
    public void A_pull_that_cannot_reach_the_engine_moves_to_disconnected_and_fails_with_the_message()
    {
        var pulling = Pulling(WithImages(), "nginx");

        var (state, _) = Reduce(pulling, new PullFailed("nginx:latest", pulling.Pulls["nginx:latest"].Generation, "connection reset", true));

        Assert.Equal(ConnectionStatus.Disconnected, state.Status);
        Assert.Equal(PullStatus.Failed, state.Pulls["nginx:latest"].Status);
        Assert.Equal("connection reset", state.Pulls["nginx:latest"].Error);
    }

    [Fact]
    public void Losing_the_connection_interrupts_the_running_pulls()
    {
        var pulling = Pulling(Pulling(WithImages(), "nginx"), "alpine");
        var (done, _) = Reduce(pulling, new PullFinished("alpine:latest", pulling.Pulls["alpine:latest"].Generation));

        var (state, _) = Reduce(done, new ConnectionLost("The engine closed the connection."));

        Assert.Equal(PullStatus.Interrupted, state.Pulls["nginx:latest"].Status);
        Assert.Null(state.Pulls["nginx:latest"].Error);
        Assert.Equal(PullStatus.Done, state.Pulls["alpine:latest"].Status);
    }

    [Fact]
    public void Starting_the_connection_over_interrupts_the_running_pulls_which_can_be_pulled_again()
    {
        var pulling = Pulling(WithImages(), "nginx");

        var (state, _) = Reduce(pulling, new ConnectRequested());
        Assert.Equal(PullStatus.Interrupted, state.Pulls["nginx:latest"].Status);

        var (connected, _) = Reduce(state, new ConnectSucceeded(Version, Info));
        var (again, effects) = Reduce(connected, new PullRequested("nginx"));
        Assert.Equal(PullStatus.Running, again.Pulls["nginx:latest"].Status);
        Assert.Single(effects.OfType<StartPullEffect>());
        Assert.Empty(Reduce(state, new PullDismissed("nginx:latest")).State.Pulls);
    }

    // Running a container

    [Fact]
    public void Run_container_creates_and_starts_once_at_a_time()
    {
        var connected = Connected();

        var (running, effects) = Reduce(connected, new RunContainerRequested(RunRequest));

        Assert.Equal(new ContainerRun("alpine", RunStatus.InProgress, null, null), running.Run);
        Assert.Equal([new RunContainerEffect(RunRequest)], effects);

        var (again, againEffects) = Reduce(running, new RunContainerRequested(RunRequest with { Name = "other" }));
        Assert.Same(running, again);
        Assert.Empty(againEffects);
    }

    [Fact]
    public void A_started_container_carries_its_id_and_reloads_the_containers()
    {
        var (running, _) = Reduce(Connected(), new RunContainerRequested(RunRequest));

        var (state, effects) = Reduce(running, new RunSucceeded("c1"));

        Assert.Equal(new ContainerRun("alpine", RunStatus.Started, "c1", null), state.Run);
        Assert.Equal([new ScheduleReloadEffect(SessionReducer.ReloadDebounce)], effects);
    }

    [Fact]
    public void A_refused_create_keeps_the_engine_message_for_the_dialog()
    {
        var (running, _) = Reduce(Connected(), new RunContainerRequested(RunRequest));

        var (state, _) = Reduce(running, new RunFailed(null, "Conflict. The container name \"/berth-it-x\" is already in use", false));

        Assert.Equal(new ContainerRun("alpine", RunStatus.Failed, null, "Conflict. The container name \"/berth-it-x\" is already in use"), state.Run);
        Assert.Null(state.ActionError);
    }

    [Fact]
    public void A_start_that_fails_after_the_create_says_the_container_exists_with_its_id()
    {
        var (running, _) = Reduce(Connected(), new RunContainerRequested(RunRequest));

        var (state, effects) = Reduce(running, new RunFailed("c1", "driver failed programming external connectivity: port is already allocated", false));

        Assert.Equal(
            new ContainerRun("alpine", RunStatus.StartFailed, "c1", "driver failed programming external connectivity: port is already allocated"),
            state.Run);
        Assert.Equal([new ScheduleReloadEffect(SessionReducer.ReloadDebounce)], effects);
    }

    [Fact]
    public void A_run_result_is_dismissed_but_not_while_in_progress()
    {
        var (running, _) = Reduce(Connected(), new RunContainerRequested(RunRequest));
        Assert.Same(running, Reduce(running, new RunDismissed()).State);

        var (failed, _) = Reduce(running, new RunFailed(null, "No such image: alpine", false));
        var (state, _) = Reduce(failed, new RunDismissed());

        Assert.Null(state.Run);
        var (next, effects) = Reduce(state, new RunContainerRequested(RunRequest));
        Assert.Equal(RunStatus.InProgress, next.Run?.Status);
        Assert.Single(effects);
    }

    [Fact]
    public void Losing_the_connection_during_a_run_interrupts_it()
    {
        var (running, _) = Reduce(Connected(), new RunContainerRequested(RunRequest));

        var (state, _) = Reduce(running, new ConnectionLost("connection reset"));

        Assert.Equal(new ContainerRun("alpine", RunStatus.Interrupted, null, null), state.Run);
    }

    [Fact]
    public void Starting_the_connection_over_during_a_run_interrupts_it_and_keeps_the_created_id()
    {
        var (running, _) = Reduce(Connected(), new RunContainerRequested(RunRequest));
        var (created, effects) = Reduce(running, new RunCreated("c1"));
        Assert.Equal(new ContainerRun("alpine", RunStatus.InProgress, "c1", null), created.Run);
        Assert.Empty(effects);

        var (state, _) = Reduce(created, new ConnectRequested());

        Assert.Equal(new ContainerRun("alpine", RunStatus.Interrupted, "c1", null), state.Run);
        Assert.Null(Reduce(state, new RunDismissed()).State.Run);
    }

    [Fact]
    public void A_run_that_cannot_reach_the_engine_moves_to_disconnected()
    {
        var (running, _) = Reduce(Connected(), new RunContainerRequested(RunRequest));

        var (state, _) = Reduce(running, new RunFailed(null, "connection reset", true));

        Assert.Equal(ConnectionStatus.Disconnected, state.Status);
        Assert.Equal(new ContainerRun("alpine", RunStatus.Failed, null, "connection reset"), state.Run);
    }

    /// <summary>Connected, with all three lists wanted and loaded.</summary>
    private static EngineState AllListsLoaded()
    {
        var state = WithImages();
        var (volumes, volumeEffects) = Reduce(state, new VolumesWanted());
        state = Reduce(volumes, new VolumesLoaded(((LoadResourceEffect)volumeEffects.Single()).Generation, [Volume("data"), Volume("logs")])).State;
        var (networks, networkEffects) = Reduce(state, new NetworksWanted());
        return Reduce(networks, new NetworksLoaded(((LoadResourceEffect)networkEffects.Single()).Generation, [Network("n1"), Network("bridge")])).State;
    }

    private static ListStatus StatusOf(EngineState state, ResourceKind kind) => kind switch
    {
        ResourceKind.Images => state.Images.Status,
        ResourceKind.Volumes => state.Volumes.Status,
        _ => state.Networks.Status,
    };
}

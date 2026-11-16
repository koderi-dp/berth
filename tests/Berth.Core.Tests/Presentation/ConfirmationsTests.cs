using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using static Berth.Tests.Presentation.ResourceFixtures;

namespace Berth.Tests.Presentation;

public sealed class ConfirmationsTests
{
    private static IReadOnlyList<ImageRow> ImageRowsOf(params ImageSummary[] images) => ImageRows.From(StateWith(images: images));

    /// <summary>A "Remove unused" item: skipped if a container uses it by its turn.</summary>
    private static RemovalItem Unused(string id, params string[] keys) => new(id, keys.Length == 0 ? [id] : [.. keys], OnlyIfUnused: true);

    private static RemovalPlan RemoveImages(IReadOnlyList<ImageRow> rows, params string[] keys) =>
        Confirmations.RemoveImages(rows, rows, keys, connected: true, usage: null);

    // Images

    [Fact]
    public void Removing_one_tag_of_an_image_with_other_tags_removes_only_that_tag()
    {
        var rows = ImageRowsOf(Image("sha256:a", ["nginx:latest", "nginx:1.27"]));

        var plan = RemoveImages(rows, "nginx:1.27");

        Assert.Equal<RemovalItem>([new RemovalItem("sha256:a", ["nginx:1.27"])], plan.Items);
        Assert.Equal("Remove tag?", plan.Title);
        Assert.Equal("The tag nginx:1.27 will be removed. The image keeps its other tags.", plan.Text);
    }

    [Fact]
    public void Removing_the_last_tag_of_an_image_removes_the_image_by_id()
    {
        var plan = RemoveImages(ImageRowsOf(Image("sha256:a", ["nginx:latest"])), "nginx:latest");

        Assert.Equal<RemovalItem>([new RemovalItem("sha256:a")], plan.Items);
        Assert.Equal("Remove image?", plan.Title);
        Assert.Equal("nginx:latest will be deleted from this engine.", plan.Text);
    }

    [Fact]
    public void Removing_an_untagged_image_removes_it_by_id()
    {
        var plan = RemoveImages(ImageRowsOf(Image("sha256:aaaabbbbccccdddd")), "sha256:aaaabbbbccccdddd");

        Assert.Equal<RemovalItem>([new RemovalItem("sha256:aaaabbbbccccdddd")], plan.Items);
        Assert.Equal("<none>:<none> (aaaabbbbcccc) will be deleted from this engine.", plan.Text);
    }

    [Fact]
    public void Removing_every_tag_of_an_image_sends_its_tags_as_one_item_in_list_order_and_never_forces()
    {
        var rows = ImageRowsOf(Image("sha256:a", ["nginx:latest", "nginx:1.27"]), Image("sha256:b", ["redis:7"]));

        var plan = RemoveImages(rows, "redis:7", "nginx:1.27", "nginx:latest");

        Assert.Equal<RemovalItem>([new RemovalItem("sha256:b"), new RemovalItem("sha256:a", ["nginx:latest", "nginx:1.27"])], plan.Items);
        var sent = Assert.IsType<RemoveRequested>(plan.ToEvent());
        Assert.Equal(ResourceKind.Images, sent.Kind);
        Assert.Equal<RemovalItem>(plan.Items, sent.Items);
        Assert.Equal<string>(["sha256:b", "nginx:latest", "nginx:1.27"], plan.Keys);
        Assert.Equal("Remove 2 images?", plan.Title);
        Assert.Equal("These will be removed:\nredis:7\nnginx:1.27\nnginx:latest", plan.Text);
    }

    [Fact]
    public void Whether_an_image_keeps_other_tags_is_read_from_the_whole_list_not_the_rows_shown()
    {
        var all = ImageRowsOf(Image("sha256:a", ["nginx:latest", "nginx:1.27"]));
        var shown = ImageRows.Shown(all, new ImageFilter("1.27", ImageShow.All), ImageRows.DefaultSort);

        var plan = Confirmations.RemoveImages(shown, all, ["nginx:1.27"], connected: true, usage: null);

        Assert.Equal<RemovalItem>([new RemovalItem("sha256:a", ["nginx:1.27"])], plan.Items);
        Assert.Equal("The tag nginx:1.27 will be removed. The image keeps its other tags.", plan.Text);
    }

    [Fact]
    public void A_removal_names_ten_rows_then_sums_up_the_rest()
    {
        var rows = ImageRowsOf([.. Enumerable.Range(1, 12).Select(i => Image($"sha256:{i:00}", [$"app:{i:00}"]))]);

        var plan = RemoveImages(rows, [.. rows.Select(r => r.Key)]);

        var lines = plan.Text.Split('\n');
        Assert.Equal("These will be removed:", lines[0]);
        Assert.Equal([.. Enumerable.Range(1, 10).Select(i => $"app:{i:00}")], lines[1..11]);
        Assert.Equal("and 2 more", lines[11]);
        Assert.Equal(12, plan.Items.Length);
        Assert.Equal("Remove 12 images?", plan.Title);
    }

    [Fact]
    public void A_removal_says_how_much_the_deleted_images_free_when_the_disk_use_is_known()
    {
        var rows = ImageRowsOf(Image("sha256:a", ["a:1"]), Image("sha256:b", ["b:1", "b:2"]));
        var usage = Usage(images: [new ImageDiskUsage("sha256:a", 4096, 1024, 3072, 0), new ImageDiskUsage("sha256:b", 1 << 20, 0, 1 << 20, 0)]);

        var plan = Confirmations.RemoveImages(rows, rows, ["a:1", "b:1"], connected: true, usage);

        // b keeps its tag b:2, so only a is deleted.
        Assert.Equal<RemovalItem>([new RemovalItem("sha256:a"), new RemovalItem("sha256:b", ["b:1"])], plan.Items);
        Assert.Equal(
            "These will be removed:\na:1\nb:1\nImages that keep other tags lose only the tag.\n3 KB will be freed; 1 KB is shared with other images and stays.",
            plan.Text);
    }

    [Fact]
    public void Deleting_several_images_says_at_least_their_own_sizes_are_freed()
    {
        var rows = ImageRowsOf(Image("sha256:a", ["a:1"]), Image("sha256:b", ["b:1"]));
        var usage = Usage(images: [new ImageDiskUsage("sha256:a", 4096, 1024, 3072, 0), new ImageDiskUsage("sha256:b", 2048, 1024, 1024, 0)]);

        var plan = Confirmations.RemoveImages(rows, rows, ["a:1", "b:1"], connected: true, usage);

        Assert.Equal("These will be removed:\na:1\nb:1\nAt least 4 KB will be freed.", plan.Text);
    }

    [Fact]
    public void Deleting_one_image_with_nothing_shared_says_only_what_is_freed()
    {
        var rows = ImageRowsOf(Image("sha256:a", ["a:1"]));
        var usage = Usage(images: [new ImageDiskUsage("sha256:a", 4096, 0, 4096, 0)]);

        var plan = Confirmations.RemoveImages(rows, rows, ["a:1"], connected: true, usage);

        Assert.Equal("a:1 will be deleted from this engine.\n4 KB will be freed.", plan.Text);
    }

    [Fact]
    public void Deleting_an_image_a_container_uses_names_the_containers_and_says_the_engine_will_refuse()
    {
        var state = StateWith(
            images: [Image("sha256:a", ["a:1"], containers: 1), Image("sha256:b", ["b:1", "b:2"], containers: 1), Image("sha256:c", ["c:1"])],
            containers: [User("web", imageId: "sha256:a"), User("job", imageId: "sha256:b")]);
        var rows = ImageRows.From(state);

        var one = RemoveImages(rows, "a:1");
        var untag = RemoveImages(rows, "b:1");
        var several = RemoveImages(rows, "a:1", "c:1");

        Assert.Equal("a:1 will be deleted from this engine.\nUsed by web. The engine will refuse while a container uses it.", one.Text);
        // Removing one of several tags only untags: the engine allows it while the image is in use.
        Assert.Equal("The tag b:1 will be removed. The image keeps its other tags.", untag.Text);
        Assert.Equal(
            "These will be removed:\na:1\nc:1\nIn use, so the engine will refuse them while a container uses them:\na:1 (used by web)",
            several.Text);
    }

    [Fact]
    public void A_removal_says_nothing_about_space_when_any_deleted_size_is_unknown()
    {
        var rows = ImageRowsOf(Image("sha256:a", ["a:1"]));
        var usage = Usage(images: [new ImageDiskUsage("sha256:a", 4096, null, null, 0)]);

        var plan = Confirmations.RemoveImages(rows, rows, ["a:1"], connected: true, usage);

        Assert.Equal("a:1 will be deleted from this engine.", plan.Text);
    }

    [Fact]
    public void At_confirm_only_rows_the_filter_shows_then_still_listed_and_not_pending_are_sent()
    {
        var state = Pending(
            StateWith(images: [Image("sha256:a", ["web:1"]), Image("sha256:b", ["db-redis:7"]), Image("sha256:c", ["db-mysql:8"]), Image("sha256:d", ["cache:16"])]),
            ResourceKind.Images,
            "sha256:c");
        var all = ImageRows.From(state);
        var shown = ImageRows.Shown(all, new ImageFilter("db-", ImageShow.All), ImageRows.DefaultSort); // web and cache are hidden

        var plan = Confirmations.RemoveImages(shown, all, ["web:1", "db-redis:7", "db-mysql:8"], connected: true, usage: null);
        Assert.Equal<RemovalItem>([new RemovalItem("sha256:b")], plan.Items);

        // Between opening the dialog and confirming, the image went away.
        var later = ImageRows.From(StateWith(images: [Image("sha256:a", ["web:1"])]));
        Assert.True(Confirmations.RemoveImages(later, later, ["db-redis:7"], connected: true, usage: null).IsEmpty);
        Assert.True(Confirmations.RemoveImages(shown, all, ["db-redis:7"], connected: false, usage: null).IsEmpty);
    }

    [Fact]
    public void A_tag_whose_image_has_another_tag_being_removed_is_not_sent()
    {
        var state = Pending(StateWith(images: [Image("sha256:a", ["a:1", "a:2"])]), ResourceKind.Images, "a:1");
        var rows = ImageRows.From(state);

        Assert.True(RemoveImages(rows, "a:2").IsEmpty);
    }

    // Volumes and networks

    [Fact]
    public void Removing_volumes_names_them_warns_of_the_data_and_says_the_size_when_known()
    {
        var state = StateWith(volumes: [Volume("data"), Volume("logs")], usage: Usage(volumes: [new VolumeDiskUsage("data", 2048, 0), new VolumeDiskUsage("logs", 1024, 0)]));
        var rows = VolumeRows.From(state);

        var one = Confirmations.RemoveVolumes(rows, ["data"], connected: true);
        var both = Confirmations.RemoveVolumes(rows, ["data", "logs"], connected: true);

        Assert.Equal("Remove volume?", one.Title);
        Assert.Equal("data will be removed. The data in it is lost for good.\n2 KB will be freed.", one.Text);
        Assert.Equal("Remove 2 volumes?", both.Title);
        Assert.Equal("2 volumes will be removed:\ndata\nlogs\nThe data in them is lost for good.\n3 KB will be freed.", both.Text);
        var sent = Assert.IsType<RemoveRequested>(both.ToEvent());
        Assert.Equal(ResourceKind.Volumes, sent.Kind);
        Assert.Equal<RemovalItem>([new RemovalItem("data"), new RemovalItem("logs")], sent.Items);
    }

    [Fact]
    public void Removing_a_volume_a_container_uses_names_the_containers_and_says_the_engine_will_refuse()
    {
        var state = StateWith(
            volumes: [Volume("berth-ref-vol"), Volume("logs")],
            containers: [User("berth-ref-voluser", volumes: ["berth-ref-vol"]), User("backup", volumes: ["berth-ref-vol"])]);
        var rows = VolumeRows.From(state);

        var one = Confirmations.RemoveVolumes(rows, ["berth-ref-vol"], connected: true);
        var both = Confirmations.RemoveVolumes(rows, ["berth-ref-vol", "logs"], connected: true);
        var unused = Confirmations.RemoveVolumes(rows, ["logs"], connected: true);

        Assert.Equal(
            "berth-ref-vol will be removed. The data in it is lost for good.\nUsed by backup, berth-ref-voluser. The engine will refuse while a container uses it.",
            one.Text);
        Assert.Equal(
            "2 volumes will be removed:\nberth-ref-vol\nlogs\nThe data in them is lost for good.\n"
            + "In use, so the engine will refuse them while a container uses them:\nberth-ref-vol (used by backup, berth-ref-voluser)",
            both.Text);
        Assert.Equal("logs will be removed. The data in it is lost for good.", unused.Text);
    }

    [Fact]
    public void A_volume_the_filter_no_longer_shows_is_not_removed()
    {
        var state = StateWith(volumes: [Volume("data"), Volume("logs")], containers: [User("web", volumes: ["data"])]);
        var shown = VolumeRows.Shown(VolumeRows.From(state), new UsageFilter("", UsageShow.Unused), VolumeRows.DefaultSort);

        var plan = Confirmations.RemoveVolumes(shown, ["data", "logs"], connected: true);

        Assert.Equal<string>(["logs"], plan.Keys);
    }

    [Fact]
    public void Removing_networks_sends_their_ids_and_never_a_built_in_one()
    {
        var rows = NetworkRows.From(StateWith(networks: [Network("id-bridge", "bridge"), Network("id-app", "app")]));

        var plan = Confirmations.RemoveNetworks(rows, ["id-bridge", "id-app"], connected: true, containersKnown: true);

        Assert.Equal<RemovalItem>([new RemovalItem("id-app")], plan.Items);
        Assert.Equal("app will be removed.", plan.Text);
        Assert.Equal(ResourceKind.Networks, Assert.IsType<RemoveRequested>(plan.ToEvent()).Kind);
    }

    [Fact]
    public void Removing_a_network_containers_are_attached_to_warns_and_names_them()
    {
        var containers = Enumerable.Range(1, 12).Select(i => User($"c{i:00}", networks: ["app"])).Append(User("db", networks: ["data"]));
        var rows = NetworkRows.From(StateWith(networks: [Network("id-app", "app"), Network("id-data", "data")], containers: containers));

        var one = Confirmations.RemoveNetworks(rows, ["id-data"], connected: true, containersKnown: true);
        var both = Confirmations.RemoveNetworks(rows, ["id-app", "id-data"], connected: true, containersKnown: true);

        Assert.Equal("data will be removed.\nThese containers are attached and will lose this network:\ndb", one.Text);
        var lines = both.Text.Split('\n');
        Assert.Equal("These containers are attached and will lose these networks:", lines[3]);
        Assert.Equal([.. Enumerable.Range(1, 10).Select(i => $"c{i:00}")], lines[4..14]);
        Assert.Equal("and 3 more", lines[14]);
    }

    [Fact]
    public void Removing_a_network_while_the_container_list_is_not_known_says_the_attached_containers_are_not_known()
    {
        var rows = NetworkRows.From(StateWith(networks: [Network("id-app", "app")]));

        var plan = Confirmations.RemoveNetworks(rows, ["id-app"], connected: true, containersKnown: false);

        Assert.Equal("app will be removed.\nThe container list has not been read, so the containers attached are not known.", plan.Text);
        Assert.Equal<RemovalItem>([new RemovalItem("id-app")], plan.Items);
    }

    // Remove unused

    [Fact]
    public void Removing_unused_names_every_item_however_many()
    {
        var state = StateWith(volumes: [.. Enumerable.Range(1, 40).Select(i => Volume($"v{i:00}"))]);

        var plan = Confirmations.PruneVolumes(state);

        var lines = plan.Text.Split('\n');
        Assert.Equal("40 volumes that no container uses will be removed:", lines[0]);
        Assert.Equal([.. Enumerable.Range(1, 40).Select(i => $"v{i:00}")], lines[1..41]);
        Assert.Equal("The data in them is lost for good.", lines[41]);
        Assert.DoesNotContain(lines, l => l.EndsWith(" more", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_unused_while_the_list_is_narrowed_says_it_is_engine_wide()
    {
        var state = StateWith(
            images: [Image("sha256:b", ["b:1"])], volumes: [Volume("old")], networks: [Network("4", "old")]);

        Assert.Equal(
            "1 volume that no container uses will be removed:\nold\nThis is every unused volume on the engine, not only the rows shown.\nThe data in it is lost for good.",
            Confirmations.PruneVolumes(state, listNarrowed: true).Text);
        Assert.Equal(
            "1 image that no container uses will be removed:\nb:1\nThis is every unused image on the engine, not only the rows shown.",
            Confirmations.PruneImages(state, danglingOnly: false, listNarrowed: true).Text);
        Assert.Equal(
            "1 network that no container uses will be removed:\nold\nThis is every unused network on the engine, not only the rows shown.",
            Confirmations.PruneNetworks(state, listNarrowed: true).Text);
        Assert.Equal("1 network that no container uses will be removed:\nold", Confirmations.PruneNetworks(state).Text);
    }

    [Fact]
    public void Removing_unused_images_sends_a_multi_tag_image_as_its_tags_in_order_and_any_other_by_id()
    {
        var state = StateWith(
            images: [Image("sha256:a", ["nginx:latest", "nginx:1"], containers: 1), Image("sha256:b", ["redis:7", "redis:latest"]), Image("sha256:cccccccccccccc")],
            containers: [User("web", imageId: "sha256:a")],
            usage: Usage(imageReclaimable: 5 << 20));

        var plan = Confirmations.PruneImages(state, danglingOnly: false);

        Assert.Equal("Remove unused images?", plan.Title);
        Assert.Equal("2 images that no container uses will be removed:\nredis:7, redis:latest\n<none>:<none> (cccccccccccc)\n5 MB will be freed.", plan.Text);
        Assert.Equal<RemovalItem>([Unused("sha256:b", "redis:7", "redis:latest"), Unused("sha256:cccccccccccccc")], [.. plan.Items.Select(i => i.Item)]);
        var sent = Assert.IsType<RemoveRequested>(plan.ToEvent());
        Assert.Equal(ResourceKind.Images, sent.Kind);
        Assert.Equal<RemovalItem>([Unused("sha256:b", "redis:7", "redis:latest"), Unused("sha256:cccccccccccccc")], sent.Items);
    }

    [Fact]
    public void Removing_dangling_images_names_only_the_untagged_unused_ones()
    {
        var state = StateWith(images: [Image("sha256:b", ["redis:7"]), Image("sha256:cccccccccccccc")]);

        var plan = Confirmations.PruneImages(state, danglingOnly: true);

        Assert.Equal("Remove dangling images?", plan.Title);
        Assert.Equal("1 untagged image that no container uses will be removed:\n<none>:<none> (cccccccccccc)", plan.Text);
        Assert.Equal<RemovalItem>([Unused("sha256:cccccccccccccc")], [.. plan.Items.Select(i => i.Item)]);
    }

    [Fact]
    public void Removing_unused_volumes_names_them_as_the_list_does_warns_of_the_data_and_says_what_it_frees()
    {
        var state = StateWith(
            volumes: [Volume("data"), Volume("old"), Volume(AnonymousName, anonymous: true)],
            containers: [User("web", volumes: ["data"])],
            usage: Usage(volumeReclaimable: 2048));

        var plan = Confirmations.PruneVolumes(state);

        Assert.Equal("Remove unused volumes?", plan.Title);
        Assert.Equal("2 volumes that no container uses will be removed:\nold\n0123456789ab\nThe data in them is lost for good.\n2 KB will be freed.", plan.Text);
        Assert.Equal<RemovalItem>([Unused("old"), Unused(AnonymousName)], [.. plan.Items.Select(i => i.Item)]);
    }

    [Fact]
    public void Removing_unused_networks_sends_ids_and_leaves_built_in_ones_and_those_a_stopped_container_joins()
    {
        var state = StateWith(
            networks: [Network("1", "bridge"), Network("2", "host"), Network("3", "app"), Network("4", "old"), Network("5", "idle")],
            containers: [User("web", networks: ["app"], state: "running"), User("job", networks: ["idle"], state: "exited")]);

        var plan = Confirmations.PruneNetworks(state);

        Assert.Equal("Remove unused networks?", plan.Title);
        Assert.Equal("1 network that no container uses will be removed:\nold", plan.Text);
        Assert.Equal<string>(["old"], plan.Names);
        Assert.Equal<RemovalItem>([Unused("4")], [.. plan.Items.Select(i => i.Item)]);
    }

    [Fact]
    public void Removing_unused_at_confirm_drops_what_is_gone_in_use_or_pending_and_adds_nothing()
    {
        var before = StateWith(volumes: [Volume("a"), Volume("b"), Volume("c"), Volume("d")]);
        var named = Confirmations.PruneVolumes(before);
        Assert.Equal(4, named.Items.Length);

        var now = Pending(
            StateWith(volumes: [Volume("b"), Volume("c"), Volume("d"), Volume("e")], containers: [User("web", volumes: ["c"])]),
            ResourceKind.Volumes,
            "d");

        var confirmed = Confirmations.ConfirmPrune(named, now);

        Assert.Equal<RemovalItem>([Unused("b")], [.. confirmed.Items.Select(i => i.Item)]);
        Assert.Equal("1 volume that no container uses will be removed:\nb\nThe data in it is lost for good.", confirmed.Text);
        Assert.True(Confirmations.ConfirmPrune(named, before with { Status = ConnectionStatus.Disconnected }).IsEmpty);
    }

    [Fact]
    public void Removing_unused_images_at_confirm_keeps_an_image_by_id_and_its_tags_as_they_are_now()
    {
        var named = Confirmations.PruneImages(StateWith(images: [Image("sha256:a", ["a:1", "a:2"]), Image("sha256:b", ["b:1"])]), danglingOnly: false);
        Assert.Equal<RemovalItem>([Unused("sha256:a", "a:1", "a:2"), Unused("sha256:b")], [.. named.Items.Select(i => i.Item)]);

        var confirmed = Confirmations.ConfirmPrune(named, StateWith(images: [Image("sha256:a", ["a:2"]), Image("sha256:b", ["b:1"], containers: 1)]));

        Assert.Equal<RemovalItem>([Unused("sha256:a")], [.. confirmed.Items.Select(i => i.Item)]);
    }

    [Fact]
    public void Removing_unused_with_nothing_to_remove_says_so()
    {
        var state = StateWith(volumes: [Volume("data")], containers: [User("web", volumes: ["data"])]);

        var plan = Confirmations.PruneVolumes(state);

        Assert.True(plan.IsEmpty);
        Assert.Equal("No volume is unused.", plan.Text);
        Assert.True(Confirmations.PruneImages(StateWith(), danglingOnly: true).IsEmpty);
        Assert.Equal("No image is dangling.", Confirmations.PruneImages(StateWith(), danglingOnly: true).Text);
        Assert.Equal("No image is unused.", Confirmations.PruneImages(StateWith(), danglingOnly: false).Text);
        Assert.Equal("No network is unused.", Confirmations.PruneNetworks(StateWith()).Text);
    }

    [Fact]
    public void What_is_unused_is_known_only_once_this_connections_container_list_has_been_read()
    {
        var loaded = StateWith(volumes: [Volume("data")], networks: [Network("1", "app")], images: [Image("sha256:a", ["a:1"])]);
        Assert.Null(Confirmations.UnusedNotKnown(loaded));

        var notLoaded = loaded with { ListLoaded = false };
        Assert.Equal("Waiting for the container list, which tells what is in use.", Confirmations.UnusedNotKnown(notLoaded));

        var failed = loaded with { ContainersLoadError = "list broke" };
        Assert.Equal("The container list could not be read, so what is in use is not known.", Confirmations.UnusedNotKnown(failed));
        Assert.Equal(Confirmations.UnusedNotKnown(failed), Confirmations.UnusedNotKnown(failed with { ListLoaded = false }));
    }

    [Theory]
    [InlineData(ResourceKind.Images, "image")]
    [InlineData(ResourceKind.Volumes, "volume")]
    [InlineData(ResourceKind.Networks, "network")]
    public void What_is_unused_is_known_only_once_the_kinds_own_list_has_been_read_on_this_connection(ResourceKind kind, string noun)
    {
        var loaded = StateWith(volumes: [Volume("data")], networks: [Network("1", "app")], images: [Image("sha256:a", ["a:1"])]);
        Assert.Null(Confirmations.UnusedNotKnown(loaded, kind));

        var notLoaded = WithListStatus(loaded, kind, s => s with { Loaded = false });
        var failed = WithListStatus(loaded, kind, s => s with { LoadError = "list broke" });
        var failedBeforeAny = WithListStatus(loaded, kind, s => s with { Loaded = false, LoadError = "list broke" });

        Assert.Equal($"Waiting for the {noun} list.", Confirmations.UnusedNotKnown(notLoaded, kind));
        Assert.Equal($"The {noun} list could not be read, so what is unused is not known.", Confirmations.UnusedNotKnown(failed, kind));
        Assert.Equal(Confirmations.UnusedNotKnown(failed, kind), Confirmations.UnusedNotKnown(failedBeforeAny, kind));
        // The container list comes first: without it nothing is known.
        Assert.Equal(Confirmations.UnusedNotKnown(loaded with { ListLoaded = false }), Confirmations.UnusedNotKnown(notLoaded with { ListLoaded = false }, kind));

        PrunePlan Plan(EngineState s) => kind switch
        {
            ResourceKind.Images => Confirmations.PruneImages(s, danglingOnly: false),
            ResourceKind.Volumes => Confirmations.PruneVolumes(s),
            _ => Confirmations.PruneNetworks(s),
        };

        Assert.False(Plan(loaded).IsEmpty);
        foreach (var unknown in new[] { notLoaded, failed })
        {
            Assert.True(Plan(unknown).IsEmpty);
            Assert.Equal(Confirmations.UnusedNotKnown(unknown, kind), Plan(unknown).Text);
            Assert.True(Confirmations.ConfirmPrune(Plan(loaded), unknown).IsEmpty);
        }
    }

    private static EngineState WithListStatus(EngineState state, ResourceKind kind, Func<ListStatus, ListStatus> change) => kind switch
    {
        ResourceKind.Images => state with { Images = state.Images with { Status = change(state.Images.Status) } },
        ResourceKind.Volumes => state with { Volumes = state.Volumes with { Status = change(state.Volumes.Status) } },
        _ => state with { Networks = state.Networks with { Status = change(state.Networks.Status) } },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Removing_unused_names_nothing_and_says_why_while_what_is_unused_is_not_known(bool failed)
    {
        var loaded = StateWith(volumes: [Volume("data")], networks: [Network("1", "app")], images: [Image("sha256:a", ["a:1"])]);
        var unknown = failed ? loaded with { ContainersLoadError = "list broke" } : loaded with { ListLoaded = false };
        var reason = Confirmations.UnusedNotKnown(unknown);

        PrunePlan[] plans = [Confirmations.PruneVolumes(unknown), Confirmations.PruneNetworks(unknown), Confirmations.PruneImages(unknown, danglingOnly: false)];

        Assert.All(plans, plan => Assert.True(plan.IsEmpty));
        Assert.All(plans, plan => Assert.Equal(reason, plan.Text));
    }

    [Fact]
    public void Removing_unused_at_confirm_sends_nothing_once_what_is_unused_is_not_known()
    {
        var loaded = StateWith(volumes: [Volume("data")], networks: [Network("1", "app")], images: [Image("sha256:a", ["a:1"])]);
        PrunePlan[] named = [Confirmations.PruneVolumes(loaded), Confirmations.PruneNetworks(loaded), Confirmations.PruneImages(loaded, danglingOnly: false)];
        Assert.All(named, plan => Assert.False(plan.IsEmpty));

        Assert.All(named, plan => Assert.True(Confirmations.ConfirmPrune(plan, loaded with { ListLoaded = false }).IsEmpty));
        Assert.All(named, plan => Assert.True(Confirmations.ConfirmPrune(plan, loaded with { ContainersLoadError = "list broke" }).IsEmpty));
    }
}

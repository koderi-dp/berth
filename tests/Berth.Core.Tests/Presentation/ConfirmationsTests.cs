using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using static Berth.Tests.Presentation.ResourceFixtures;

namespace Berth.Tests.Presentation;

public sealed class ConfirmationsTests
{
    private static IReadOnlyList<ImageRow> ImageRowsOf(params ImageSummary[] images) => ImageRows.From(StateWith(images: images));

    // Images

    [Fact]
    public void Removing_one_tag_of_an_image_with_other_tags_removes_only_that_tag()
    {
        var rows = ImageRowsOf(Image("sha256:a", ["nginx:latest", "nginx:1.27"]));

        var plan = Confirmations.RemoveImages(rows, ["nginx:1.27"], connected: true, usage: null);

        Assert.Equal<string>(Sent("nginx:1.27"), plan.Keys);
        Assert.Equal("Remove image?", plan.Title);
        Assert.Equal("The tag nginx:1.27 will be removed. The image keeps its other tags.", plan.Text);
    }

    [Fact]
    public void Removing_the_last_tag_of_an_image_removes_the_image_by_id()
    {
        var rows = ImageRowsOf(Image("sha256:a", ["nginx:latest"]));

        var plan = Confirmations.RemoveImages(rows, ["nginx:latest"], connected: true, usage: null);

        Assert.Equal<string>(Sent("sha256:a"), plan.Keys);
        Assert.Equal("nginx:latest will be deleted from this engine.", plan.Text);
    }

    [Fact]
    public void Removing_an_untagged_image_removes_it_by_id()
    {
        var rows = ImageRowsOf(Image("sha256:aaaabbbbccccdddd"));

        var plan = Confirmations.RemoveImages(rows, ["sha256:aaaabbbbccccdddd"], connected: true, usage: null);

        Assert.Equal<string>(Sent("sha256:aaaabbbbccccdddd"), plan.Keys);
        Assert.Equal("<none>:<none> (aaaabbbbcccc) will be deleted from this engine.", plan.Text);
    }

    [Fact]
    public void Removing_every_tag_of_an_image_removes_each_tag_and_never_forces()
    {
        var rows = ImageRowsOf(Image("sha256:a", ["nginx:latest", "nginx:1.27"]), Image("sha256:b", ["redis:7"]));

        var plan = Confirmations.RemoveImages(rows, ["nginx:latest", "nginx:1.27", "redis:7"], connected: true, usage: null);

        Assert.Equal<string>(Sent("nginx:latest", "nginx:1.27", "sha256:b"), plan.Keys);
        Assert.Equal(new RemoveImagesRequested(plan.Keys, Force: false), plan.ToEvent());
        Assert.Equal("Remove 3 images?", plan.Title);
        Assert.Equal("3 images will be removed:\nnginx:latest\nnginx:1.27\nredis:7", plan.Text);
    }

    [Fact]
    public void A_removal_names_ten_rows_then_sums_up_the_rest()
    {
        var images = Enumerable.Range(1, 12).Select(i => Image($"sha256:{i:00}", [$"app:{i:00}"])).ToArray();
        var rows = ImageRowsOf(images);

        var plan = Confirmations.RemoveImages(rows, rows.Select(r => r.Key), connected: true, usage: null);

        var lines = plan.Text.Split('\n');
        Assert.Equal("12 images will be removed:", lines[0]);
        Assert.Equal([.. Enumerable.Range(1, 10).Select(i => $"app:{i:00}")], lines[1..11]);
        Assert.Equal("and 2 more", lines[11]);
        Assert.Equal(12, plan.Keys.Length);
    }

    [Fact]
    public void A_removal_says_how_much_it_frees_when_the_disk_use_is_known()
    {
        var rows = ImageRowsOf(Image("sha256:a", ["a:1"]), Image("sha256:b", ["b:1", "b:2"]));
        var usage = Usage(images: [new ImageDiskUsage("sha256:a", 4096, 1024, 3072, 0), new ImageDiskUsage("sha256:b", 1 << 20, 0, 1 << 20, 0)]);

        var plan = Confirmations.RemoveImages(rows, ["a:1", "b:1"], connected: true, usage);

        // b keeps its tag b:2, so only a is deleted.
        Assert.Equal<string>(Sent("sha256:a", "b:1"), plan.Keys);
        Assert.Equal("2 images will be removed:\na:1\nb:1\nImages that keep other tags lose only the tag.\n3 KB will be freed.", plan.Text);
    }

    [Fact]
    public void A_removal_says_nothing_about_space_when_any_deleted_size_is_unknown()
    {
        var rows = ImageRowsOf(Image("sha256:a", ["a:1"]));
        var usage = Usage(images: [new ImageDiskUsage("sha256:a", 4096, null, null, 0)]);

        var plan = Confirmations.RemoveImages(rows, ["a:1"], connected: true, usage);

        Assert.Equal("a:1 will be deleted from this engine.", plan.Text);
    }

    [Fact]
    public void Only_checked_rows_the_filter_shows_are_removed_and_never_pending_or_gone_ones()
    {
        var state = Pending(
            StateWith(images: [Image("sha256:a", ["web:1"]), Image("sha256:b", ["db-redis:7"]), Image("sha256:c", ["db-mysql:8"]), Image("sha256:d", ["cache:16"])]),
            ResourceKind.Images,
            "sha256:c");
        var all = ImageRows.From(state);
        var shown = ImageRows.Shown(all, new ImageFilter("db-", ImageShow.All), ImageRows.DefaultSort); // web and cache are hidden

        var keys = RowSelection.Checked(shown, Keys("web:1", "db-redis:7", "db-mysql:8"), connected: true);
        Assert.Equal<string>(Sent("db-redis:7"), keys);

        // Between opening the dialog and confirming, the image went away.
        var later = ImageRows.From(StateWith(images: [Image("sha256:a", ["web:1"])]));
        Assert.True(Confirmations.RemoveImages(later, keys, connected: true, usage: null).IsEmpty);
        Assert.True(Confirmations.RemoveImages(all, keys, connected: false, usage: null).IsEmpty);
        Assert.Empty(Confirmations.RemoveImages(all, ["db-mysql:8"], connected: true, usage: null).Keys);
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
        Assert.Equal<string>(["data", "logs"], Assert.IsType<RemoveVolumesRequested>(both.ToEvent()).Names);
    }

    [Fact]
    public void Removing_networks_sends_their_ids_and_never_a_built_in_one()
    {
        var rows = NetworkRows.From(StateWith(networks: [Network("id-bridge", "bridge"), Network("id-app", "app")]));

        var plan = Confirmations.RemoveNetworks(rows, ["id-bridge", "id-app"], connected: true);

        Assert.Equal<string>(Sent("id-app"), plan.Keys);
        Assert.Equal("app will be removed.", plan.Text);
        Assert.Equal<string>(["id-app"], Assert.IsType<RemoveNetworksRequested>(plan.ToEvent()).Ids);
    }

    // Prune

    [Fact]
    public void Pruning_images_names_every_unused_image_and_says_what_it_frees()
    {
        var state = StateWith(
            images: [Image("sha256:a", ["nginx:latest", "nginx:1"], containers: 1), Image("sha256:b", ["redis:7"]), Image("sha256:cccccccccccccc")],
            containers: [User("web", imageId: "sha256:a")],
            usage: Usage(imageReclaimable: 5 << 20));

        var plan = Confirmations.PruneImages(state, danglingOnly: false);

        Assert.Equal("Remove unused images?", plan.Title);
        Assert.Equal("2 images that no container uses will be removed:\nredis:7\n<none>:<none> (cccccccccccc)\n5 MB will be freed.", plan.Text);
        Assert.Equal(new PruneRequested(ResourceKind.Images, DanglingImagesOnly: false), plan.ToEvent());
    }

    [Fact]
    public void Pruning_dangling_images_names_only_the_untagged_unused_ones()
    {
        var state = StateWith(images: [Image("sha256:b", ["redis:7"]), Image("sha256:cccccccccccccc")]);

        var plan = Confirmations.PruneImages(state, danglingOnly: true);

        Assert.Equal("Remove dangling images?", plan.Title);
        Assert.Equal("1 untagged image that no container uses will be removed:\n<none>:<none> (cccccccccccc)", plan.Text);
        Assert.Equal(new PruneRequested(ResourceKind.Images, DanglingImagesOnly: true), plan.ToEvent());
    }

    [Fact]
    public void Pruning_volumes_names_the_unused_ones_warns_of_the_data_and_says_what_it_frees()
    {
        var state = StateWith(
            volumes: [Volume("data"), Volume("old"), Volume(AnonymousName, anonymous: true)],
            containers: [User("web", volumes: ["data"])],
            usage: Usage(volumeReclaimable: 2048));

        var plan = Confirmations.PruneVolumes(state);

        Assert.Equal("Remove unused volumes?", plan.Title);
        Assert.Equal(
            $"2 volumes that no container uses will be removed:\nold\n{AnonymousName}\nThe data in them is lost for good.\n2 KB will be freed.",
            plan.Text);
        Assert.Equal(new PruneRequested(ResourceKind.Volumes), plan.ToEvent());
    }

    [Fact]
    public void Pruning_networks_names_the_unused_ones_that_are_not_built_in()
    {
        var state = StateWith(
            networks: [Network("1", "bridge"), Network("2", "host"), Network("3", "app"), Network("4", "old")],
            containers: [User("web", networks: ["app"])]);

        var plan = Confirmations.PruneNetworks(state);

        Assert.Equal("Remove unused networks?", plan.Title);
        Assert.Equal("1 network that no container uses will be removed:\nold", plan.Text);
        Assert.Equal<string>(["old"], plan.Names);
    }

    [Fact]
    public void A_prune_with_nothing_to_remove_says_so()
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
}

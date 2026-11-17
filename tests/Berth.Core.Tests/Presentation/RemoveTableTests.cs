using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Presentation;
using Berth.State;
using static Berth.Tests.Presentation.ResourceFixtures;

namespace Berth.Tests.Presentation;

public sealed class RemoveTableTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static RemoveTableRow Row(
        string key,
        RemoveSize size = RemoveSize.NotRead,
        long bytes = 0,
        string? inUseBy = null,
        bool untagOnly = false,
        IReadOnlyList<string>? attached = null,
        string? scope = null) =>
        new(key, [key], key, key, "second", scope, Now.AddHours(-2), inUseBy, untagOnly, attached) { Size = size, Bytes = bytes };

    private static RemoveTable Table(ResourceKind kind, params RemoveTableRow[] rows) => new(kind, "lead", rows) { SizesRead = true };

    private static RemoveTable Images(params RemoveTableRow[] rows) => Table(ResourceKind.Images, rows);

    private static IReadOnlySet<string> All(RemoveTable table) => table.AllKeys;

    // Rows from the plans

    [Fact]
    public void Removing_unused_images_has_a_row_per_image_with_its_tags_on_one_row()
    {
        var state = StateWith(images:
        [
            Image("sha256:aaaabbbbccccdddd", ["a:1", "a:2"], created: 1_700_000_000),
            Image("sha256:eeeeffff00001111"),
            Image("sha256:used0000used0000", ["used:1"], containers: 1),
        ]);

        var table = Confirmations.PruneImages(state, danglingOnly: false).Table!;

        Assert.Equal(ResourceKind.Images, table.Kind);
        Assert.Equal<string>(["sha256:aaaabbbbccccdddd", "sha256:eeeeffff00001111"], [.. table.Rows.Select(r => r.Key)]);
        Assert.Equal("a:1, a:2", table.Rows[0].Name);
        Assert.Equal("a:1\na:2", table.Rows[0].FullName);
        Assert.Equal("aaaabbbbcccc", table.Rows[0].Second);
        Assert.Equal<string>(["sha256:aaaabbbbccccdddd"], table.Rows[0].SendKeys);
        Assert.Null(table.Rows[0].Scope);
        Assert.Equal("Untagged", table.Rows[1].Name);
        Assert.Equal("Untagged image sha256:eeeeffff00001111", table.Rows[1].FullName);
        Assert.Equal("Image ID", table.SecondTitle);
        Assert.Equal("Created", table.ThirdTitle);
        Assert.True(table.HasSize);
        Assert.Equal(table.AllKeys, [.. table.Rows.Select(r => r.Key)]);
    }

    [Fact]
    public void The_lead_says_what_the_table_is_and_that_the_rows_can_be_unticked()
    {
        var state = StateWith(images: [Image("sha256:a", ["a:1"]), Image("sha256:b")]);

        Assert.Equal("Images no container uses. Untick any you want to keep.", Confirmations.PruneImages(state, danglingOnly: false).Table!.Lead);
        Assert.Equal("Untagged images no container uses. Untick any you want to keep.", Confirmations.PruneImages(state, danglingOnly: true).Table!.Lead);
        Assert.EndsWith(
            "This is every unused image on the engine, not only the rows shown.",
            Confirmations.PruneImages(state, danglingOnly: false, listNarrowed: true).Table!.Lead);
    }

    [Fact]
    public void Removing_unused_volumes_has_a_row_per_volume_with_its_driver()
    {
        var state = StateWith(
            volumes: [Volume("data", "local", Now.AddDays(-3)), Volume("mounted")],
            containers: [User("web", volumes: ["mounted"])]);

        var table = Confirmations.PruneVolumes(state).Table!;

        var row = Assert.Single(table.Rows);
        Assert.Equal(ResourceKind.Volumes, table.Kind);
        Assert.Equal("data", row.Key);
        Assert.Equal<string>(["data"], row.SendKeys);
        Assert.Equal("local", row.Second);
        Assert.Equal("3 days ago", row.ThirdText(Now));
        Assert.Equal("Driver", table.SecondTitle);
        Assert.Equal("Created", table.ThirdTitle);
        Assert.True(table.HasSize);
        Assert.True(table.EngineTotalWhenAll);
    }

    [Fact]
    public void Removing_unused_networks_has_a_row_per_network_with_its_driver_and_scope_and_no_size_column()
    {
        var state = StateWith(networks: [Network("n1", "old", "bridge", "local"), Network("n2", "swarm-net", "overlay", "swarm")]);

        var table = Confirmations.PruneNetworks(state).Table!;

        Assert.Equal(ResourceKind.Networks, table.Kind);
        Assert.Equal<string>(["n1", "n2"], [.. table.Rows.Select(r => r.Key)]);
        Assert.Equal("old", table.Rows[0].Name);
        Assert.Equal("bridge", table.Rows[0].Second);
        Assert.Equal("local", table.Rows[0].ThirdText(Now));
        Assert.Equal("overlay", table.Rows[1].Second);
        Assert.Equal("Scope", table.ThirdTitle);
        Assert.False(table.HasSize);
    }

    [Fact]
    public void Nothing_to_remove_has_no_table()
    {
        Assert.Null(Confirmations.PruneVolumes(StateWith()).Table);
        Assert.Null(Confirmations.PruneImages(StateWith(), danglingOnly: false).Table);
        Assert.Null(Confirmations.PruneNetworks(StateWith()).Table);
    }

    [Fact]
    public void Deleting_one_item_has_no_table_and_several_have_one_with_the_rows_in_order()
    {
        var state = StateWith(volumes: [Volume("a"), Volume("b"), Volume("c")]);
        var rows = VolumeRows.From(state);

        Assert.Null(Confirmations.RemoveVolumes(rows, ["b"], connected: true).Table);

        var table = Confirmations.RemoveVolumes(rows, ["a", "c"], connected: true).Table!;
        Assert.Equal<string>(["a", "c"], [.. table.Rows.Select(r => r.Key)]);
        Assert.Contains("the data in them is lost for good", table.Lead);
    }

    [Fact]
    public void Deleting_a_volume_a_container_mounts_says_who_uses_it()
    {
        var state = StateWith(volumes: [Volume("data"), Volume("free")], containers: [User("web", volumes: ["data"])]);

        var table = Confirmations.RemoveVolumes(VolumeRows.From(state), ["data", "free"], connected: true).Table!;

        Assert.Equal("used by web", table.Rows[0].InUseBy);
        Assert.Null(table.Rows[1].InUseBy);
    }

    [Fact]
    public void Deleting_several_images_has_one_row_per_image_sending_the_checked_tags()
    {
        var rows = ImageRows.From(StateWith(images: [Image("sha256:a", ["a:1"]), Image("sha256:b", ["b:1", "b:2"])]));

        var table = Confirmations.RemoveImages(rows, rows, ["a:1", "b:1", "b:2"], connected: true, usage: null).Table!;

        Assert.Equal<string>(["sha256:a", "sha256:b"], [.. table.Rows.Select(r => r.Key)]);
        Assert.Equal<string>(["a:1"], table.Rows[0].SendKeys);
        Assert.Equal<string>(["b:1", "b:2"], table.Rows[1].SendKeys);
        Assert.Equal("b:1, b:2", table.Rows[1].Name);
        Assert.False(table.Rows[1].UntagOnly);
    }

    [Fact]
    public void Deleting_some_tags_of_an_image_makes_a_tag_only_row_that_sends_those_tags()
    {
        var rows = ImageRows.From(StateWith(images: [Image("sha256:a", ["a:1"]), Image("sha256:b", ["b:1", "b:2"])]));
        var usage = Usage(images: [new ImageDiskUsage("sha256:a", 4096, 1024, 3072, 0), new ImageDiskUsage("sha256:b", 4096, 0, 4096, 0)]);

        var table = Confirmations.RemoveImages(rows, rows, ["a:1", "b:1"], connected: true, usage).Table!;

        Assert.True(table.Rows[1].UntagOnly);
        Assert.Equal<string>(["b:1"], table.Rows[1].SendKeys);
        Assert.Equal(RemoveSize.TagOnly, table.Rows[1].Size);
        Assert.Equal("Tag only", table.Rows[1].SizeText);
        Assert.Equal(RemoveSize.Known, table.Rows[0].Size);
        Assert.Equal("3 KB", table.Rows[0].SizeText);
        Assert.True(table.SizesRead);
    }

    [Fact]
    public void Deleting_several_networks_names_the_containers_that_lose_them()
    {
        var state = StateWith(
            networks: [Network("n1", "front"), Network("n2", "back")],
            containers: [User("web", networks: ["front"])]);
        var rows = NetworkRows.From(state);

        var table = Confirmations.RemoveNetworks(rows, ["n1", "n2"], connected: true, containersKnown: false).Table!;

        Assert.False(table.ContainersKnown);
        Assert.Equal<string>(["web"], table.Rows[0].Attached!);
        Assert.False(table.HasSize);
        Assert.Contains("The container list has not been read", table.Summary(All(table)).Notes);
        Assert.Null(Confirmations.RemoveNetworks(rows, ["n1"], connected: true, containersKnown: true).Table);
    }

    // Sizes

    [Fact]
    public void Sizes_are_not_read_until_the_disk_use_is_given()
    {
        var table = new RemoveTable(ResourceKind.Images, "lead", [Row("a")]);

        Assert.Same(table, table.WithUsage(null));
        Assert.False(table.SizesRead);
        Assert.Equal(RemoveSize.NotRead, table.Rows[0].Size);
        Assert.Equal("…", table.Rows[0].SizeText);
        Assert.Equal("Reading the disk use", table.Rows[0].SizeTip);
    }

    [Fact]
    public void An_image_row_reads_its_unique_size_and_what_it_shares()
    {
        var table = new RemoveTable(ResourceKind.Images, "lead", [Row("sha256:a")])
            .WithUsage(Usage(images: [new ImageDiskUsage("sha256:a", 4096, 1024, 3072, 0)]));

        var row = table.Rows[0];
        Assert.True(table.SizesRead);
        Assert.Equal(RemoveSize.Known, row.Size);
        Assert.Equal(3072, row.Bytes);
        Assert.Equal(1024, row.SharedBytes);
        Assert.Equal("3 KB", row.SizeText);
        Assert.Equal("Frees 3 KB; 1 KB is shared with other images and stays.", row.SizeTip);
    }

    [Fact]
    public void An_image_with_nothing_shared_frees_all_it_holds()
    {
        var row = new RemoveTable(ResourceKind.Images, "lead", [Row("sha256:a")])
            .WithUsage(Usage(images: [new ImageDiskUsage("sha256:a", 4096, 0, 4096, 0)])).Rows[0];

        Assert.Equal(RemoveSize.Known, row.Size);
        Assert.Null(row.SharedBytes);
        Assert.Equal("Frees 4 KB.", row.SizeTip);
    }

    [Fact]
    public void An_image_whose_layers_are_all_shared_reads_shared()
    {
        var row = new RemoveTable(ResourceKind.Images, "lead", [Row("sha256:a")])
            .WithUsage(Usage(images: [new ImageDiskUsage("sha256:a", 4096, 4096, 0, 0)])).Rows[0];

        Assert.Equal(RemoveSize.Shared, row.Size);
        Assert.Equal(0, row.Bytes);
        Assert.Equal(4096, row.SharedBytes);
        Assert.Equal("Shared", row.SizeText);
        Assert.Contains("shared with other images", row.SizeTip);
    }

    [Fact]
    public void An_image_with_no_unique_size_or_no_entry_is_unknown()
    {
        var table = new RemoveTable(ResourceKind.Images, "lead", [Row("sha256:a"), Row("sha256:b")])
            .WithUsage(Usage(images: [new ImageDiskUsage("sha256:a", 4096, null, null, 0)]));

        Assert.All(table.Rows, r => Assert.Equal(RemoveSize.Unknown, r.Size));
        Assert.Equal("Unknown", table.Rows[0].SizeText);
        Assert.Equal("The engine did not measure it.", table.Rows[0].SizeTip);
    }

    [Fact]
    public void A_tag_only_row_frees_nothing_whatever_the_usage_says()
    {
        var row = new RemoveTable(ResourceKind.Images, "lead", [Row("sha256:a", untagOnly: true)])
            .WithUsage(Usage(images: [new ImageDiskUsage("sha256:a", 4096, 0, 4096, 0)])).Rows[0];

        Assert.Equal(RemoveSize.TagOnly, row.Size);
        Assert.Equal(0, row.Bytes);
        Assert.Equal("Tag only", row.SizeText);
        Assert.Contains("only this tag is removed", row.SizeTip);
    }

    [Fact]
    public void A_volume_row_reads_its_size_or_is_unknown()
    {
        var table = new RemoveTable(ResourceKind.Volumes, "lead", [Row("data"), Row("remote"), Row("gone")])
            .WithUsage(Usage(volumes: [new VolumeDiskUsage("data", 2048, 0), new VolumeDiskUsage("remote", null, 0)]));

        Assert.Equal(RemoveSize.Known, table.Rows[0].Size);
        Assert.Equal("2 KB", table.Rows[0].SizeText);
        Assert.Equal(RemoveSize.Unknown, table.Rows[1].Size);
        Assert.Equal(RemoveSize.Unknown, table.Rows[2].Size);
    }

    [Fact]
    public void Networks_have_no_sizes()
    {
        var table = new RemoveTable(ResourceKind.Networks, "lead", [Row("n1")]);

        Assert.Same(table, table.WithUsage(Usage()));
        Assert.False(table.WithUsage(Usage()).SizesRead);
    }

    [Fact]
    public void The_engines_total_is_used_only_when_asked_for_and_read()
    {
        var usage = Usage(volumes: [new VolumeDiskUsage("data", 2048, 0)], volumeReclaimable: 5000, imageReclaimable: 7000);
        var volumes = new RemoveTable(ResourceKind.Volumes, "lead", [Row("data")]);

        Assert.Null(volumes.WithUsage(usage).EngineTotal);
        Assert.Equal(5000, (volumes with { EngineTotalWhenAll = true }).WithUsage(usage).EngineTotal);
        Assert.Equal(7000, (new RemoveTable(ResourceKind.Images, "lead", [Row("sha256:a")]) { EngineTotalWhenAll = true }).WithUsage(usage).EngineTotal);
    }

    [Fact]
    public void The_third_column_is_the_scope_or_the_age()
    {
        Assert.Equal("local", Row("n", scope: "local").ThirdText(Now));
        Assert.Equal("2 hours ago", Row("a").ThirdText(Now));
        Assert.Equal("Unknown", (Row("a") with { Created = null }).ThirdText(Now));
    }

    // Checked rows

    [Fact]
    public void The_select_all_box_is_true_for_all_false_for_none_and_null_for_some()
    {
        var table = Images(Row("a"), Row("b"));

        Assert.True(table.CheckedState(All(table)));
        Assert.False(table.CheckedState(Keys()));
        Assert.Null(table.CheckedState(Keys("a")));
        Assert.False(table.CheckedState(Keys("other")));
    }

    [Fact]
    public void The_keys_a_delete_sends_are_those_of_the_checked_rows_in_the_rows_order()
    {
        var table = Images(
            Row("a") with { SendKeys = ["a:1", "a:2"] },
            Row("b") with { SendKeys = ["b:1"] },
            Row("c") with { SendKeys = ["c:1"] });

        Assert.Equal<string>(["a:1", "a:2", "c:1"], table.Keys(Keys("c", "a")));
        Assert.Empty(table.Keys(Keys()));
        Assert.Equal<string>(["a:1", "a:2", "b:1", "c:1"], table.Keys(All(table)));
    }

    // Summary

    [Fact]
    public void The_summary_counts_the_checked_rows_and_names_the_button()
    {
        var table = Images(Row("a", RemoveSize.Known, 1024), Row("b", RemoveSize.Known, 2048), Row("c", RemoveSize.Known, 4096));

        var all = table.Summary(All(table));
        var two = table.Summary(Keys("a", "b"));
        var one = table.Summary(Keys("c"));
        var none = table.Summary(Keys());

        Assert.Equal("3 of 3 selected · about 7 KB will be freed", all.Line);
        Assert.Equal("Remove 3 images", all.ConfirmText);
        Assert.True(all.CanConfirm);
        Assert.Equal("2 of 3 selected · about 3 KB will be freed", two.Line);
        Assert.Equal("Remove 2 images", two.ConfirmText);
        Assert.Equal("1 of 3 selected · 4 KB will be freed", one.Line);
        Assert.Equal("Remove 1 image", one.ConfirmText);
        Assert.Equal("0 of 3 selected", none.Line);
        Assert.Equal("Remove", none.ConfirmText);
        Assert.False(none.CanConfirm);
        Assert.Null(none.Notes);
    }

    [Fact]
    public void The_summary_follows_the_rows_as_they_are_checked_and_unchecked()
    {
        var table = Images(Row("a", RemoveSize.Known, 1024), Row("b", RemoveSize.Known, 2048));
        var chosen = new HashSet<string>(All(table));

        Assert.Equal("2 of 2 selected · about 3 KB will be freed", table.Summary(chosen).Line);
        chosen.Remove("a");
        Assert.Equal("1 of 2 selected · 2 KB will be freed", table.Summary(chosen).Line);
        chosen.Remove("b");
        Assert.Equal("0 of 2 selected", table.Summary(chosen).Line);
        chosen.Add("a");
        Assert.Equal("1 of 2 selected · 1 KB will be freed", table.Summary(chosen).Line);
    }

    [Fact]
    public void Volumes_and_images_name_their_buttons_by_kind()
    {
        var volumes = Table(ResourceKind.Volumes, Row("a", RemoveSize.Known, 1024), Row("b", RemoveSize.Known, 2048));
        var networks = Table(ResourceKind.Networks, Row("a"), Row("b"));

        Assert.Equal("Remove 2 volumes", volumes.Summary(All(volumes)).ConfirmText);
        Assert.Equal("Remove 1 volume", volumes.Summary(Keys("a")).ConfirmText);
        Assert.Equal("Remove 2 networks", networks.Summary(All(networks)).ConfirmText);
        Assert.Equal("Remove 1 network", networks.Summary(Keys("a")).ConfirmText);
    }

    [Fact]
    public void Volumes_sum_to_an_exact_figure_where_images_say_about()
    {
        var volumes = Table(ResourceKind.Volumes, Row("a", RemoveSize.Known, 1024), Row("b", RemoveSize.Known, 2048));

        Assert.Equal("2 of 2 selected · 3 KB will be freed", volumes.Summary(All(volumes)).Line);
    }

    [Fact]
    public void Networks_say_no_figure()
    {
        var networks = Table(ResourceKind.Networks, Row("a"), Row("b"));

        Assert.Equal("2 of 2 selected", networks.Summary(All(networks)).Line);
    }

    [Fact]
    public void Before_the_sizes_are_read_the_summary_says_so()
    {
        var table = new RemoveTable(ResourceKind.Images, "lead", [Row("a"), Row("b")]);

        Assert.Equal("2 of 2 selected · sizes are not known yet", table.Summary(All(table)).Line);
        Assert.Equal("Remove 2 images", table.Summary(All(table)).ConfirmText);
    }

    [Fact]
    public void A_tag_only_choice_frees_no_space_and_the_button_says_tags()
    {
        var table = Images(Row("a", RemoveSize.TagOnly, untagOnly: true), Row("b", RemoveSize.TagOnly, untagOnly: true), Row("c", RemoveSize.Known, 1024));

        var tags = table.Summary(Keys("a", "b"));
        var one = table.Summary(Keys("a"));
        var mixed = table.Summary(Keys("a", "c"));

        Assert.Equal("2 of 3 selected · no space will be freed", tags.Line);
        Assert.Equal("Remove 2 tags", tags.ConfirmText);
        Assert.Equal("Remove 1 tag", one.ConfirmText);
        Assert.Equal("Remove 2 images", mixed.ConfirmText);
        Assert.Equal("2 of 3 selected · 1 KB will be freed", mixed.Line);
        Assert.Equal("Images that keep other tags lose only the tag.", tags.Notes);
    }

    [Fact]
    public void An_unmeasured_choice_says_the_space_is_not_known_and_a_partly_unmeasured_one_says_about()
    {
        var table = Images(Row("a", RemoveSize.Unknown), Row("b", RemoveSize.Unknown), Row("c", RemoveSize.Known, 2048), Row("d", RemoveSize.TagOnly, untagOnly: true));

        Assert.Equal("2 of 4 selected · the space freed is not known", table.Summary(Keys("a", "b")).Line);
        Assert.Equal("2 of 4 selected · the space freed is not known", table.Summary(Keys("a", "d")).Line);
        Assert.Equal("2 of 4 selected · about 2 KB will be freed", table.Summary(Keys("a", "c")).Line);
    }

    [Fact]
    public void Shared_images_free_little_or_nothing()
    {
        var table = Images(Row("a", RemoveSize.Shared), Row("b", RemoveSize.Shared), Row("c", RemoveSize.Known, 2048));

        Assert.Equal("2 of 3 selected · little or no space will be freed", table.Summary(Keys("a", "b")).Line);
        Assert.Equal("2 of 3 selected · about 2 KB will be freed", table.Summary(Keys("a", "c")).Line);
    }

    [Fact]
    public void A_known_row_that_frees_nothing_says_little_or_none()
    {
        var table = Table(ResourceKind.Volumes, Row("a", RemoveSize.Known, 0), Row("b", RemoveSize.Known, 2048));

        Assert.Equal("1 of 2 selected · little or no space will be freed", table.Summary(Keys("a")).Line);
    }

    [Fact]
    public void With_every_row_checked_the_engines_own_total_replaces_the_sum()
    {
        var table = Images(Row("a", RemoveSize.Known, 1024), Row("b", RemoveSize.Known, 2048)) with { EngineTotal = 5000 };

        Assert.Equal("2 of 2 selected · 4.88 KB will be freed", table.Summary(All(table)).Line);
        Assert.Equal("1 of 2 selected · 1 KB will be freed", table.Summary(Keys("a")).Line);
        Assert.Equal("2 of 2 selected · no space will be freed", (table with { EngineTotal = 0 }).Summary(All(table)).Line);
    }

    [Fact]
    public void The_engines_total_is_not_used_before_the_sizes_are_read()
    {
        var table = new RemoveTable(ResourceKind.Images, "lead", [Row("a"), Row("b")]) { EngineTotal = 5000 };

        Assert.Equal("2 of 2 selected · sizes are not known yet", table.Summary(All(table)).Line);
    }

    // Notes

    [Fact]
    public void A_row_in_use_is_noted_as_refused_by_the_engine()
    {
        var table = Images(Row("a", inUseBy: "used by web-1"), Row("b"));

        Assert.Equal("a: used by web-1. The engine will refuse while a container uses it.", table.Summary(All(table)).Notes);
        Assert.Null(table.Summary(Keys("b")).Notes);
    }

    [Fact]
    public void Several_rows_in_use_are_noted_together_naming_at_most_ten()
    {
        var two = Images(Row("a", inUseBy: "used by x"), Row("b", inUseBy: "used by y"));
        Assert.Equal(
            "The engine will refuse these while a container uses them: a (used by x), b (used by y).",
            two.Summary(All(two)).Notes);

        var twelve = Images([.. Enumerable.Range(1, 12).Select(i => Row($"r{i:00}", inUseBy: "used by x"))]);
        var notes = twelve.Summary(All(twelve)).Notes!;
        Assert.EndsWith("r10 (used by x) and 2 more.", notes);
        Assert.DoesNotContain("r11", notes);
    }

    [Fact]
    public void A_tag_only_row_in_use_is_not_noted_as_refused()
    {
        var table = Images(Row("a", RemoveSize.TagOnly, inUseBy: "used by web-1", untagOnly: true));

        Assert.Equal("Images that keep other tags lose only the tag.", table.Summary(All(table)).Notes);
    }

    [Fact]
    public void Networks_note_the_containers_that_lose_them_once_each()
    {
        var one = Table(ResourceKind.Networks, Row("a", attached: ["web", "db"]));
        Assert.Equal("These containers are attached and will lose the network: web, db.", one.Summary(All(one)).Notes);

        var two = Table(ResourceKind.Networks, Row("a", attached: ["web", "db"]), Row("b", attached: ["web"]));
        Assert.Equal("These containers are attached and will lose these networks: web, db.", two.Summary(All(two)).Notes);
        Assert.Equal("These containers are attached and will lose the network: web.", two.Summary(Keys("b")).Notes);
        Assert.Null(Table(ResourceKind.Networks, Row("a")).Summary(Keys("a")).Notes);
    }

    [Fact]
    public void Networks_say_when_the_container_list_was_not_read()
    {
        var table = Table(ResourceKind.Networks, Row("a")) with { ContainersKnown = false };

        Assert.Equal("The container list has not been read, so the containers attached are not known.", table.Summary(All(table)).Notes);
        Assert.Null(table.Summary(Keys()).Notes);
    }

    // The plan built from the checked rows

    [Fact]
    public void Keeping_the_checked_rows_narrows_a_prune_plan_in_its_order()
    {
        var state = StateWith(volumes: [Volume("a"), Volume("b"), Volume("c")]);
        var named = Confirmations.PruneVolumes(state);

        var kept = named.Keeping(Keys("c", "a"));

        Assert.Equal<string>(["a", "c"], [.. kept.Items.Select(i => i.Id)]);
        Assert.Equal<string>(["a", "b", "c"], [.. named.Items.Select(i => i.Id)]);
        Assert.Equal<RemovalItem>(
            [new RemovalItem("a", ["a"], OnlyIfUnused: true), new RemovalItem("c", ["c"], OnlyIfUnused: true)],
            [.. Confirmations.ConfirmPrune(kept, state).Items.Select(i => i.Item)]);
        Assert.True(named.Keeping(Keys()).Items.IsEmpty);
    }

    [Fact]
    public void Confirming_the_kept_rows_plans_again_against_the_state_then()
    {
        var before = StateWith(volumes: [Volume("a"), Volume("b"), Volume("c")]);
        var kept = Confirmations.PruneVolumes(before).Keeping(Keys("a", "b"));

        var now = StateWith(volumes: [Volume("a"), Volume("b"), Volume("c")], containers: [User("web", volumes: ["b"])]);

        Assert.Equal<string>(["a"], [.. Confirmations.ConfirmPrune(kept, now).Items.Select(i => i.Id)]);
    }

    [Fact]
    public void An_image_prune_keeps_the_checked_images_by_id()
    {
        var state = StateWith(images: [Image("sha256:a", ["a:1", "a:2"]), Image("sha256:b", ["b:1"])]);
        var table = Confirmations.PruneImages(state, danglingOnly: false).Table!;

        var confirmed = Confirmations.ConfirmPrune(Confirmations.PruneImages(state, danglingOnly: false).Keeping(Keys("sha256:b")), state);

        Assert.Equal<string>(["sha256:b"], [.. confirmed.Items.Select(i => i.Id)]);
        Assert.Equal<string>(["sha256:a", "sha256:b"], [.. table.Rows.Select(r => r.Key)]);
    }

    [Fact]
    public void A_delete_sends_the_keys_of_the_rows_still_checked()
    {
        var rows = ImageRows.From(StateWith(images: [Image("sha256:a", ["a:1"]), Image("sha256:b", ["b:1", "b:2"]), Image("sha256:c", ["c:1"])]));
        var table = Confirmations.RemoveImages(rows, rows, ["a:1", "b:1", "b:2", "c:1"], connected: true, usage: null).Table!;

        var keys = table.Keys(Keys("sha256:c", "sha256:b"));
        var plan = Confirmations.RemoveImages(rows, rows, keys, connected: true, usage: null);

        Assert.Equal<string>(["b:1", "b:2", "c:1"], keys);
        Assert.Equal(2, plan.Items.Length);
        Assert.Equal("Remove 2 images?", plan.Title);
    }

    [Fact]
    public void The_start_selection_is_every_row()
    {
        var table = Images(Row("a"), Row("b"));

        Assert.Equal<string>(["a", "b"], [.. table.AllKeys.Order()]);
        Assert.IsType<ImmutableHashSet<string>>(table.AllKeys, exactMatch: false);
        Assert.Empty(Images().AllKeys);
    }
}

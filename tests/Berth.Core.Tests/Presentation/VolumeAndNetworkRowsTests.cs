using Berth.Engine.Models;
using Berth.Presentation;
using Berth.State;
using static Berth.Tests.Presentation.ResourceFixtures;

namespace Berth.Tests.Presentation;

public sealed class VolumeAndNetworkRowsTests
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_volume_row_says_which_containers_mount_it_from_the_container_list()
    {
        var state = StateWith(
            volumes: [Volume("data", created: Monday), Volume("logs")],
            containers: [User("web", volumes: ["data"]), User("job", volumes: ["data"], bind: "/data"), User("other", volumes: ["cache"])]);

        var rows = VolumeRows.From(state);

        Assert.Equal(["job", "web"], rows[0].UsedBy);
        Assert.True(rows[0].InUse);
        Assert.Equal(Monday, rows[0].Created);
        Assert.Equal("local", rows[0].Driver);
        Assert.False(rows[1].InUse);
    }

    [Fact]
    public void A_volume_size_is_known_only_once_disk_use_is_loaded()
    {
        var volumes = new[] { Volume("data"), Volume("remote", driver: "nfs") };
        var withoutUsage = VolumeRows.From(StateWith(volumes: volumes));
        var withUsage = VolumeRows.From(StateWith(volumes: volumes, usage: Usage(volumes: [new VolumeDiskUsage("data", 2048, 1), new VolumeDiskUsage("remote", null, 0)])));

        Assert.All(withoutUsage, r => Assert.Null(r.Size));
        Assert.Equal(2048, withUsage[0].Size);
        Assert.Null(withUsage[1].Size);
    }

    [Fact]
    public void An_anonymous_volume_shows_a_short_name_and_keeps_the_full_one()
    {
        var state = StateWith(volumes: [Volume(AnonymousName, anonymous: true), Volume("data")]);

        var rows = VolumeRows.From(state);

        Assert.True(rows[0].IsAnonymous);
        Assert.Equal("0123456789ab", rows[0].ShownName);
        Assert.Equal(AnonymousName, rows[0].Name);
        Assert.False(rows[1].IsAnonymous);
        Assert.Equal("data", rows[1].ShownName);
    }

    [Fact]
    public void A_name_of_64_hex_characters_is_anonymous_even_without_the_label()
    {
        var row = Assert.Single(VolumeRows.From(StateWith(volumes: [Volume(AnonymousName)])));

        Assert.True(row.IsAnonymous);
    }

    [Theory]
    [InlineData(UsageShow.All, "", new[] { "data", "logs" })]
    [InlineData(UsageShow.InUse, "", new[] { "data" })]
    [InlineData(UsageShow.Unused, "", new[] { "logs" })]
    [InlineData(UsageShow.All, "LOG", new[] { "logs" })]
    public void Volumes_filter_by_use_and_search(UsageShow show, string search, string[] names)
    {
        var state = StateWith(volumes: [Volume("logs"), Volume("data")], containers: [User("web", volumes: ["data"])]);

        var rows = VolumeRows.Shown(VolumeRows.From(state), new UsageFilter(search, show), VolumeRows.DefaultSort);

        Assert.Equal(names, rows.Select(r => r.Name));
    }

    [Fact]
    public void Volumes_sort_by_size_with_an_unknown_size_below_any_known_one()
    {
        var state = StateWith(
            volumes: [Volume("a"), Volume("b"), Volume("c")],
            usage: Usage(volumes: [new VolumeDiskUsage("a", 300, 0), new VolumeDiskUsage("c", 100, 0)]));

        var ascending = VolumeRows.Shown(VolumeRows.From(state), UsageFilter.All, new ListSort<VolumeColumn>(VolumeColumn.Size, false));
        var descending = VolumeRows.Shown(VolumeRows.From(state), UsageFilter.All, new ListSort<VolumeColumn>(VolumeColumn.Size, true));

        Assert.Equal(["b", "c", "a"], ascending.Select(r => r.Name));
        Assert.Equal(["a", "c", "b"], descending.Select(r => r.Name));
    }

    [Fact]
    public void The_users_of_a_volume_say_where_it_is_mounted_and_how()
    {
        var state = StateWith(volumes: [Volume("data"), Volume("ro")], containers: [User("web", volumes: ["data", "ro"])]);

        var users = VolumeRows.Users(state, "ro");

        var user = Assert.Single(users);
        Assert.Equal(new VolumeUser("web", "web", "/data/ro", ReadWrite: false), user);
    }

    [Fact]
    public void A_network_row_names_the_attached_containers_and_marks_the_built_in_networks()
    {
        var state = StateWith(
            networks: [Network("id-bridge", "bridge"), Network("id-host", "host", driver: "host"), Network("id-none", "none", driver: "null"), Network("id-app", "app", created: Monday)],
            containers: [User("web", networks: ["app", "bridge"]), User("db", networks: ["app"])]);

        var rows = NetworkRows.From(state).ToDictionary(r => r.Name);

        Assert.Equal(["db", "web"], rows["app"].Containers);
        Assert.True(rows["app"].InUse);
        Assert.Equal(Monday, rows["app"].Created);
        Assert.False(rows["app"].IsBuiltIn);
        Assert.Equal("id-app", rows["app"].Key);
        Assert.True(rows["bridge"].IsBuiltIn);
        Assert.True(rows["host"].IsBuiltIn);
        Assert.True(rows["none"].IsBuiltIn);
    }

    [Fact]
    public void A_built_in_network_cannot_be_removed_or_checked_for_removal()
    {
        var state = StateWith(networks: [Network("id-bridge", "bridge"), Network("id-app", "app")]);
        var rows = NetworkRows.From(state);

        Assert.False(NetworkRows.CanRemove(rows.Single(r => r.Name == "bridge"), connected: true));
        Assert.True(NetworkRows.CanRemove(rows.Single(r => r.Name == "app"), connected: true));
        Assert.False(NetworkRows.CanRemove(rows.Single(r => r.Name == "app"), connected: false));
        Assert.Equal<string>(["id-app"], NetworkRows.RemoveKeys(rows, Keys("id-bridge", "id-app"), connected: true));
    }

    [Theory]
    [InlineData(NetworkColumn.Name, new[] { "app", "bridge", "zeta" })]
    [InlineData(NetworkColumn.Driver, new[] { "zeta", "app", "bridge" })]
    [InlineData(NetworkColumn.Containers, new[] { "bridge", "zeta", "app" })]
    public void Networks_sort_by_their_columns(NetworkColumn column, string[] names)
    {
        var state = StateWith(
            networks: [Network("1", "bridge"), Network("2", "zeta", driver: "a-overlay"), Network("3", "app", driver: "b-macvlan")],
            containers: [User("web", networks: ["app", "zeta"]), User("db", networks: ["app"])]);

        var rows = NetworkRows.Shown(NetworkRows.From(state), UsageFilter.All, new ListSort<NetworkColumn>(column, false));

        Assert.Equal(names, rows.Select(r => r.Name));
    }

    [Theory]
    [InlineData(UsageShow.InUse, "", new[] { "app" })]
    [InlineData(UsageShow.Unused, "", new[] { "bridge" })]
    [InlineData(UsageShow.All, "BRI", new[] { "bridge" })]
    public void Networks_filter_by_use_and_search(UsageShow show, string search, string[] names)
    {
        var state = StateWith(networks: [Network("1", "bridge"), Network("2", "app", driver: "overlay")], containers: [User("web", networks: ["app"])]);

        var rows = NetworkRows.Shown(NetworkRows.From(state), new UsageFilter(search, show), NetworkRows.DefaultSort);

        Assert.Equal(names, rows.Select(r => r.Name));
    }

    [Fact]
    public void The_empty_lists_say_why()
    {
        Assert.Equal("No volumes on this engine.", VolumeRows.EmptyText(engineHasVolumes: false, UsageFilter.All));
        Assert.Equal("No volumes match the search.", VolumeRows.EmptyText(engineHasVolumes: true, new UsageFilter("x", UsageShow.All)));
        Assert.Equal("No volumes are in use.", VolumeRows.EmptyText(engineHasVolumes: true, new UsageFilter("", UsageShow.InUse)));
        Assert.Equal("No volumes are unused.", VolumeRows.EmptyText(engineHasVolumes: true, new UsageFilter("", UsageShow.Unused)));
        Assert.Equal("No networks on this engine.", NetworkRows.EmptyText(engineHasNetworks: false, UsageFilter.All));
        Assert.Equal("No networks match the search.", NetworkRows.EmptyText(engineHasNetworks: true, new UsageFilter("x", UsageShow.All)));
    }

    [Fact]
    public void A_volume_or_network_row_is_pending_while_an_action_runs_on_it()
    {
        var state = Pending(Pending(StateWith(volumes: [Volume("data")], networks: [Network("n1")]), ResourceKind.Volumes, "data"), ResourceKind.Networks, "n1");

        Assert.True(Assert.Single(VolumeRows.From(state)).IsPending);
        Assert.True(Assert.Single(NetworkRows.From(state)).IsPending);
    }
}

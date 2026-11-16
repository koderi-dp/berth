using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using static Berth.Tests.Presentation.ResourceFixtures;

namespace Berth.Tests.Presentation;

/// <summary>What the Volumes and Networks pages decide beyond their rows: row actions, the usage line, the create dialog's name and a network's members.</summary>
public sealed class VolumeAndNetworkPageTests
{
    private static RowAction Find(RowActions actions, RowActionKind kind) =>
        actions.Sections.SelectMany(s => s).Single(a => a.Kind == kind);

    private static VolumeRow VolumeRow(EngineState state) => VolumeRows.From(state)[0];

    private static NetworkRow NetworkRow(EngineState state, string name) => NetworkRows.From(state).Single(r => r.Name == name);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_volume_has_view_details_and_copy_name_in_its_menu_and_delete_as_its_button(bool compact)
    {
        var actions = RowActions.ForVolume(VolumeRow(StateWith(volumes: [Volume("data")])), connected: true);

        Assert.Equal(
            [[RowActionKind.ViewDetails], [RowActionKind.CopyName], [RowActionKind.Delete]],
            actions.Sections.Select(s => s.Select(a => a.Kind).ToArray()).ToArray());
        Assert.Empty(actions.Buttons(compact));
        Assert.Equal([[RowActionKind.ViewDetails], [RowActionKind.CopyName]], actions.MoreMenu(compact).Select(s => s.Select(a => a.Kind).ToArray()).ToArray());
        Assert.True(actions.HasDelete);
        Assert.All(actions.Sections.SelectMany(s => s), a => Assert.True(a.IsEnabled));
        Assert.All(actions.Sections.SelectMany(s => s), a => Assert.Equal(["data"], a.Ids.ToArray()));
        Assert.Equal(["View details", "Copy name", "Delete"], actions.Sections.SelectMany(s => s).Select(a => a.Label));
    }

    [Fact]
    public void A_volume_cannot_be_deleted_while_removing_or_disconnected_but_can_still_be_viewed_and_copied()
    {
        var pending = RowActions.ForVolume(VolumeRow(Pending(StateWith(volumes: [Volume("data")]), ResourceKind.Volumes, "data")), connected: true);
        var offline = RowActions.ForVolume(VolumeRow(StateWith(volumes: [Volume("data")])), connected: false);

        Assert.False(pending.Delete.IsEnabled);
        Assert.False(offline.Delete.IsEnabled);
        Assert.True(Find(offline, RowActionKind.ViewDetails).IsEnabled);
        Assert.True(Find(offline, RowActionKind.CopyName).IsEnabled);
    }

    [Fact]
    public void On_the_volumes_own_page_there_is_no_view_details()
    {
        var actions = RowActions.ForVolume(VolumeRow(StateWith(volumes: [Volume("data")])), connected: true, viewDetails: false);

        Assert.Equal([[RowActionKind.CopyName], [RowActionKind.Delete]], actions.Sections.Select(s => s.Select(a => a.Kind).ToArray()).ToArray());
    }

    [Fact]
    public void A_network_has_the_copies_in_its_menu_and_delete_by_its_id()
    {
        var actions = RowActions.ForNetwork(NetworkRow(StateWith(networks: [Network("id-app", "app")]), "app"), connected: true);

        Assert.Equal(
            [[RowActionKind.CopyName, RowActionKind.CopyId], [RowActionKind.Delete]],
            actions.Sections.Select(s => s.Select(a => a.Kind).ToArray()).ToArray());
        Assert.True(actions.HasDelete);
        Assert.True(actions.Delete.IsEnabled);
        Assert.Empty(actions.Buttons(compact: false));
        Assert.All(actions.Sections.SelectMany(s => s), a => Assert.Equal(["id-app"], a.Ids.ToArray()));
    }

    [Theory]
    [InlineData("bridge")]
    [InlineData("host")]
    [InlineData("none")]
    public void A_built_in_network_has_no_delete_but_can_be_copied(string name)
    {
        var actions = RowActions.ForNetwork(NetworkRow(StateWith(networks: [Network("id-" + name, name)]), name), connected: true);

        Assert.False(actions.HasDelete);
        Assert.Equal([[RowActionKind.CopyName, RowActionKind.CopyId]], actions.Sections.Select(s => s.Select(a => a.Kind).ToArray()).ToArray());
        Assert.All(actions.Sections.SelectMany(s => s), a => Assert.True(a.IsEnabled));
    }

    [Fact]
    public void A_network_cannot_be_deleted_while_removing_or_disconnected()
    {
        var state = StateWith(networks: [Network("id-app", "app")]);

        Assert.False(RowActions.ForNetwork(NetworkRow(Pending(state, ResourceKind.Networks, "id-app"), "app"), connected: true).Delete.IsEnabled);
        Assert.False(RowActions.ForNetwork(NetworkRow(state, "app"), connected: false).Delete.IsEnabled);
    }

    [Fact]
    public void Containers_and_images_still_have_delete()
    {
        var image = ImageRows.From(StateWith(images: [Image("sha256:a", ["a:1"])]))[0];

        Assert.True(RowActions.ForImage(image, connected: true).HasDelete);
    }

    [Fact]
    public void The_volumes_usage_line_says_what_is_in_use_of_the_total_and_how_many_volumes()
    {
        var usage = new DiskUsage([], new UsageTotals(0, 0, 0, 0), [], new UsageTotals(3, 1, 1_288_490_189, 1_218_235_597));
        var state = StateWith(volumes: [Volume("a"), Volume("b"), Volume("c")], usage: usage);

        Assert.Equal("67 MB in use of 1.2 GB · 3 volumes", VolumeRows.UsageLine(state));
    }

    [Fact]
    public void Until_the_disk_use_arrives_the_volumes_line_says_it_is_being_calculated()
    {
        Assert.Equal("Calculating… · 1 volume", VolumeRows.UsageLine(StateWith(volumes: [Volume("a")])));
        Assert.Equal("Calculating… · 0 volumes", VolumeRows.UsageLine(StateWith()));
    }

    [Theory]
    [InlineData("berth-check-vol")]
    [InlineData("a")]
    [InlineData("9_data.v-1")]
    [InlineData("")]
    public void A_good_or_empty_volume_name_has_no_error(string name)
    {
        Assert.Null(VolumeRows.NameError(name, StateWith(volumes: [Volume("data")])));
    }

    [Theory]
    [InlineData("-data")]
    [InlineData(".data")]
    [InlineData("_data")]
    [InlineData("my data")]
    [InlineData("data/1")]
    [InlineData("dätä")]
    [InlineData(" data")]
    public void A_volume_name_must_start_with_a_letter_or_digit_and_hold_only_letters_digits_and_the_three_marks(string name)
    {
        Assert.Equal(RunDraft.NameError, VolumeRows.NameError(name, StateWith()));
    }

    [Fact]
    public void A_volume_name_already_listed_is_refused_before_it_is_sent()
    {
        Assert.Equal("A volume named data already exists.", VolumeRows.NameError("data", StateWith(volumes: [Volume("data")])));
        Assert.Null(VolumeRows.NameError("Data", StateWith(volumes: [Volume("data")])));
    }

    [Fact]
    public void Create_needs_a_good_name_a_connection_and_no_create_of_that_name_under_way()
    {
        var state = StateWith(volumes: [Volume("data")]);

        Assert.True(VolumeRows.CanCreate("new", state));
        Assert.False(VolumeRows.CanCreate("", state));
        Assert.False(VolumeRows.CanCreate("-new", state));
        Assert.False(VolumeRows.CanCreate("data", state));
        Assert.False(VolumeRows.CanCreate("new", StateWith(connected: false)));
        Assert.False(VolumeRows.CanCreate("new", Pending(state, ResourceKind.Volumes, "new", ItemAction.Create)));
    }

    [Fact]
    public void Create_does_not_wait_for_a_create_of_another_name_still_under_way()
    {
        var state = StateWith(volumes: [Volume("data")]);

        Assert.True(VolumeRows.CanCreate("new", state with { VolumeCreate = new VolumeCreate(1, "other", VolumeCreateStatus.InProgress) }));
        Assert.True(VolumeRows.CanCreate("new", Pending(state, ResourceKind.Volumes, "other", ItemAction.Create)));
    }

    [Theory]
    [InlineData(VolumeCreateStatus.InProgress, null)]
    [InlineData(VolumeCreateStatus.Created, null)]
    [InlineData(VolumeCreateStatus.Refused, "volume name is already in use")]
    [InlineData(VolumeCreateStatus.Interrupted, VolumeRows.CreateInterruptedText)]
    [InlineData(VolumeCreateStatus.NotConnected, "Not sent: Berth is not connected to the engine. Try again once it is.")]
    [InlineData(VolumeCreateStatus.NameBusy, "Not sent: a volume named data is being created or removed.")]
    public void A_create_that_ended_without_a_volume_says_why(VolumeCreateStatus status, string? text)
    {
        var error = status == VolumeCreateStatus.Refused ? "volume name is already in use" : null;

        Assert.Equal(text, VolumeRows.CreateFailure(new VolumeCreate(1, "data", status, error)));
    }

    [Fact]
    public void A_networks_members_are_its_attached_containers_by_name_with_their_ids()
    {
        var state = StateWith(
            networks: [Network("id-app", "app"), Network("id-bridge", "bridge")],
            containers: [User("web", networks: ["app", "bridge"]), User("db", networks: ["app"]), User("job", networks: ["bridge"])]);

        Assert.Equal([new NetworkMember("db", "db"), new NetworkMember("web", "web")], NetworkRows.Members(state, "app"));
        Assert.Equal([new NetworkMember("job", "job"), new NetworkMember("web", "web")], NetworkRows.Members(state, "bridge"));
        Assert.Empty(NetworkRows.Members(state, "other"));
    }
}

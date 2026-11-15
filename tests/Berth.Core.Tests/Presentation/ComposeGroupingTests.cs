using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using static Berth.Tests.Presentation.ContainerFixtures;

namespace Berth.Tests.Presentation;

public sealed class ComposeGroupingTests
{
    private static readonly ImmutableDictionary<string, ContainerAction> NoPending =
        ImmutableDictionary<string, ContainerAction>.Empty;

    private static IReadOnlyList<ContainerGroup> Build(params ContainerSummary[] containers) =>
        ComposeGrouping.Build([.. containers], NoPending, NoFilter);

    [Fact]
    public void Containers_with_the_same_project_label_form_one_group()
    {
        var groups = Build(
            Container("a1", "shop-web-1", project: "shop"),
            Container("b1", "blog-web-1", project: "blog"),
            Container("a2", "shop-db-1", project: "shop"));

        Assert.Equal(["blog", "shop"], groups.Select(g => g.Project));
        Assert.Equal(["a2", "a1"], groups[1].Rows.Select(r => r.Id));
        Assert.Equal(["b1"], groups[0].Rows.Select(r => r.Id));
    }

    [Fact]
    public void Containers_without_the_label_are_each_their_own_group_with_null_project()
    {
        var groups = Build(Container("a", "alpha"), Container("b", "beta"));

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Null(g.Project));
        Assert.Equal(["a"], groups[0].Rows.Select(r => r.Id));
        Assert.Equal(["b"], groups[1].Rows.Select(r => r.Id));
    }

    [Fact]
    public void Groups_are_ordered_by_name_ignoring_case_and_rows_inside_a_group_too()
    {
        var groups = Build(
            Container("1", "zeta"),
            Container("2", "Mango-db-1", project: "Mango"),
            Container("3", "mango-API-1", project: "Mango"),
            Container("4", "Alpha"),
            Container("5", "beta-x", project: "beta"));

        // Compose projects and loose containers share one ordering: Alpha, beta, Mango, zeta.
        Assert.Equal([null, "beta", "Mango", null], groups.Select(g => g.Project));
        Assert.Equal("Alpha", groups[0].Rows.Single().Name);
        Assert.Equal("zeta", groups[3].Rows.Single().Name);
        Assert.Equal(["mango-API-1", "Mango-db-1"], groups[2].Rows.Select(r => r.Name));
    }

    [Fact]
    public void A_group_is_running_when_any_row_is_running()
    {
        var groups = Build(
            Container("1", "a-1", state: "exited", project: "a"),
            Container("2", "a-2", state: "running", project: "a"),
            Container("3", "a-3", state: "paused", project: "a"),
            Container("4", "b-1", state: "exited", project: "b"),
            Container("5", "b-2", state: "paused", project: "b"),
            Container("6", "b-3", state: "restarting", project: "b"),
            Container("7", "c-1", state: "exited", project: "c"),
            Container("8", "c-2", state: "paused", project: "c"),
            Container("9", "d-1", state: "exited", project: "d"),
            Container("10", "d-2", state: "created", project: "d"));

        Assert.Equal(
            [RowState.Running, RowState.Restarting, RowState.Paused, RowState.Stopped],
            groups.Select(g => g.State));
        Assert.Equal(
            [RowState.Stopped, RowState.Running, RowState.Paused],
            groups[0].Rows.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => r.State));
    }

    [Fact]
    public void Name_drops_the_leading_slash_and_uses_the_first_name()
    {
        var row = Build(Container("abc", names: ["/web", "/alias/web"])).Single().Rows.Single();

        Assert.Equal("web", row.Name);
    }

    [Fact]
    public void A_container_with_no_names_shows_its_short_id()
    {
        var row = Build(Container("0123456789abcdef0123", names: [])).Single().Rows.Single();

        Assert.Equal("0123456789ab", row.Name);
    }

    [Fact]
    public void Short_id_is_twelve_characters()
    {
        var row = Build(Container("0123456789abcdef0123456789abcdef", "web")).Single().Rows.Single();

        Assert.Equal("0123456789ab", row.ShortId);
        Assert.Equal("0123456789abcdef0123456789abcdef", row.Id);
    }

    [Fact]
    public void Pending_action_is_carried_onto_the_row()
    {
        var pending = ImmutableDictionary<string, ContainerAction>.Empty.Add("a", ContainerAction.Stop);

        var groups = ComposeGrouping.Build([Container("a", "alpha"), Container("b", "beta")], pending, NoFilter);

        Assert.Equal(ContainerAction.Stop, groups[0].Rows.Single().Pending);
        Assert.Null(groups[1].Rows.Single().Pending);
    }

    [Theory]
    [InlineData("shop-web-1", "shop", "web-1")]
    [InlineData("shop_web_1", "shop", "web_1")]
    [InlineData("Shop-web-1", "shop", "web-1")]
    [InlineData("custom-name", "shop", "custom-name")]
    [InlineData("shop", "shop", "shop")]
    [InlineData("shop-", "shop", "shop-")]
    [InlineData("shopping-web-1", "shop", "shopping-web-1")]
    public void Name_under_a_project_drops_the_project_prefix(string name, string project, string shown) =>
        Assert.Equal(shown, ComposeGrouping.NameInProject(name, project));

    [Fact]
    public void Row_carries_image_status_ports_and_created_time()
    {
        var container = Container(
            "a", "web", image: "nginx:1.27", created: 1_700_000_000,
            ports: [new PortMapping("0.0.0.0", 80, 8080, "tcp")]);

        var row = Build(container).Single().Rows.Single();

        Assert.Equal("nginx:1.27", row.Image);
        Assert.Equal("Up 1 minute", row.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), row.Created);
        Assert.Equal([new PortLink("8080:80", new Uri("http://localhost:8080"))], row.Ports);
    }
}

using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using static Berth.Tests.Presentation.ContainerFixtures;

namespace Berth.Tests.Presentation;

public sealed class ContainerFilterTests
{
    private static readonly ImmutableArray<ContainerSummary> Containers =
    [
        Container("aaaa1111bbbb2222", "web", image: "nginx:latest"),
        Container("cccc3333dddd4444", "cache", state: "exited", image: "redis:7"),
        Container("eeee5555ffff6666", "shop-api-1", project: "Shop", image: "shop/api:dev"),
        Container("9999000011112222", "shop-db-1", state: "exited", project: "Shop", image: "postgres:17"),
    ];

    private static IReadOnlyList<ContainerGroup> Build(string search, bool runningOnly = false) =>
        ComposeGrouping.Build(Containers, ImmutableDictionary<string, ContainerAction>.Empty, new ContainerFilter(search, runningOnly));

    private static string[] RowIds(IReadOnlyList<ContainerGroup> groups) =>
        [.. groups.SelectMany(g => g.Rows).Select(r => r.Id)];

    [Fact]
    public void Search_matches_name_image_id_and_project_ignoring_case()
    {
        Assert.Equal(["aaaa1111bbbb2222"], RowIds(Build("WEB")));
        Assert.Equal(["cccc3333dddd4444"], RowIds(Build("Redis")));
        Assert.Equal(["cccc3333dddd4444"], RowIds(Build("CCCC3333")));
        Assert.Equal(["eeee5555ffff6666", "9999000011112222"], RowIds(Build("sHoP")));
        Assert.Equal(["9999000011112222"], RowIds(Build("postgres")));
        Assert.Empty(Build("nothing-matches"));
    }

    [Fact]
    public void A_group_matching_by_project_name_keeps_all_its_rows()
    {
        var groups = ComposeGrouping.Build(
            [
                Container("1", "frontend", project: "acme"),
                Container("2", "backend", project: "acme"),
                Container("3", "acme-tools"),
                Container("4", "other"),
            ],
            ImmutableDictionary<string, ContainerAction>.Empty,
            new ContainerFilter("ACME", RunningOnly: false));

        Assert.Equal(["acme", null], groups.Select(g => g.Project));
        Assert.Equal(["2", "1"], groups[0].Rows.Select(r => r.Id));
        Assert.Equal(["3"], groups[1].Rows.Select(r => r.Id));
    }

    [Fact]
    public void Running_only_hides_stopped_rows_and_then_empty_groups()
    {
        var groups = ComposeGrouping.Build(
            [
                Container("1", "web", state: "running"),
                Container("2", "cache", state: "exited"),
                Container("3", "app-1", state: "paused", project: "app"),
                Container("4", "app-2", state: "exited", project: "app"),
                Container("5", "job-1", state: "restarting", project: "job"),
                Container("6", "old-1", state: "exited", project: "old"),
                Container("7", "old-2", state: "created", project: "old"),
            ],
            ImmutableDictionary<string, ContainerAction>.Empty,
            new ContainerFilter("", RunningOnly: true));

        Assert.Equal(["app", "job", null], groups.Select(g => g.Project));
        Assert.Equal(["3"], groups[0].Rows.Select(r => r.Id));
        Assert.Equal(["5"], groups[1].Rows.Select(r => r.Id));
        Assert.Equal(["1"], groups[2].Rows.Select(r => r.Id));
    }

    [Fact]
    public void Running_only_applies_to_a_group_matching_by_project_name()
    {
        var groups = Build("shop", runningOnly: true);

        Assert.Equal(["eeee5555ffff6666"], RowIds(groups));
    }

    [Fact]
    public void Blank_search_matches_everything()
    {
        Assert.Equal(4, RowIds(Build("")).Length);
        Assert.Equal(4, RowIds(Build("   ")).Length);
    }
}

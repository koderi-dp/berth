using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class ContainerSortTests
{
    private static ContainerRow Row(string name, string image = "alpine", RowState state = RowState.Running, long created = 0) =>
        new(name + "-id", name, name, image, state, "", [], DateTimeOffset.FromUnixTimeSeconds(created), null);

    private static ContainerGroup Single(ContainerRow row) => new(null, row.State, [row]);

    private static ContainerGroup Project(string project, params ContainerRow[] rows) => new(project, RowState.Running, rows);

    private static string[] Names(IReadOnlyList<ContainerGroup> groups) =>
        [.. groups.SelectMany(g => g.Project is null ? [g.Rows[0].Name] : (string[])["[" + g.Project + "]", .. g.Rows.Select(r => r.Name)])];

    private static readonly IReadOnlyList<ContainerGroup> Groups =
    [
        Project("shop", Row("shop-web", "nginx", RowState.Running, created: 300), Row("shop-db", "postgres", RowState.Stopped, created: 100)),
        Single(Row("Alpha", "redis", RowState.Paused, created: 200)),
        Single(Row("zeta", "busybox", RowState.Stopped, created: 400)),
    ];

    [Fact]
    public void The_default_is_name_ascending() =>
        Assert.Equal(new ContainerSort(SortColumn.Name, Descending: false), ContainerSort.Default);

    [Fact]
    public void Name_ascending_orders_projects_by_their_name_among_the_rows_ignoring_case() =>
        Assert.Equal(["Alpha", "[shop]", "shop-db", "shop-web", "zeta"], Names(ContainerSort.Default.Apply(Groups)));

    [Fact]
    public void Name_descending_reverses_groups_and_the_rows_inside_them() =>
        Assert.Equal(["zeta", "[shop]", "shop-web", "shop-db", "Alpha"], Names(new ContainerSort(SortColumn.Name, true).Apply(Groups)));

    [Fact]
    public void Image_sorts_rows_and_places_a_project_by_its_first_row_after_sorting() =>
        // shop's rows sort nginx, postgres: its key is nginx, between busybox and redis.
        Assert.Equal(["zeta", "[shop]", "shop-web", "shop-db", "Alpha"], Names(new ContainerSort(SortColumn.Image, false).Apply(Groups)));

    [Fact]
    public void Image_descending_uses_the_first_row_after_sorting_descending() =>
        // shop's rows sort postgres, nginx: its key is postgres, after redis.
        Assert.Equal(["Alpha", "[shop]", "shop-db", "shop-web", "zeta"], Names(new ContainerSort(SortColumn.Image, true).Apply(Groups)));

    [Fact]
    public void Status_sorts_running_then_restarting_then_paused_then_stopped()
    {
        IReadOnlyList<ContainerGroup> groups =
        [
            Single(Row("a", state: RowState.Stopped)),
            Single(Row("b", state: RowState.Paused)),
            Single(Row("c", state: RowState.Running)),
            Single(Row("d", state: RowState.Restarting)),
        ];

        Assert.Equal(["c", "d", "b", "a"], Names(new ContainerSort(SortColumn.Status, false).Apply(groups)));
        Assert.Equal(["a", "b", "d", "c"], Names(new ContainerSort(SortColumn.Status, true).Apply(groups)));
    }

    [Fact]
    public void Created_ascending_is_oldest_first() =>
        Assert.Equal(["[shop]", "shop-db", "shop-web", "Alpha", "zeta"], Names(new ContainerSort(SortColumn.Created, false).Apply(Groups)));

    [Fact]
    public void Created_descending_is_newest_first() =>
        Assert.Equal(["zeta", "[shop]", "shop-web", "shop-db", "Alpha"], Names(new ContainerSort(SortColumn.Created, true).Apply(Groups)));

    [Fact]
    public void Equal_keys_fall_back_to_the_name_ascending_in_either_direction()
    {
        IReadOnlyList<ContainerGroup> groups = [Single(Row("b")), Single(Row("c")), Single(Row("a"))];

        Assert.Equal(["a", "b", "c"], Names(new ContainerSort(SortColumn.Image, false).Apply(groups)));
        Assert.Equal(["a", "b", "c"], Names(new ContainerSort(SortColumn.Image, true).Apply(groups)));
    }

    [Fact]
    public void Clicking_the_sorted_column_reverses_it() =>
        Assert.Equal(new ContainerSort(SortColumn.Name, true), ContainerSort.Default.Toggle(SortColumn.Name));

    [Fact]
    public void Clicking_another_column_sorts_by_it_ascending() =>
        Assert.Equal(new ContainerSort(SortColumn.Created, false), new ContainerSort(SortColumn.Name, true).Toggle(SortColumn.Created));

    [Fact]
    public void The_accessible_name_says_the_state()
    {
        var sort = new ContainerSort(SortColumn.Name, false);

        Assert.Equal("Name, sorted ascending", sort.AccessibleName(SortColumn.Name));
        Assert.Equal("Name, sorted descending", sort.Toggle(SortColumn.Name).AccessibleName(SortColumn.Name));
        Assert.Equal("Image, not sorted", sort.AccessibleName(SortColumn.Image));
    }

    [Theory]
    [InlineData("Name", false, SortColumn.Name, false)]
    [InlineData("Created", true, SortColumn.Created, true)]
    [InlineData("created", null, SortColumn.Created, false)]
    [InlineData(null, true, SortColumn.Name, true)]
    [InlineData("Ports", false, SortColumn.Name, false)]
    [InlineData("7", false, SortColumn.Name, false)]
    [InlineData("Name,Image", false, SortColumn.Name, false)]
    public void Reading_a_saved_sort_falls_back_to_the_default_for_what_is_missing_or_unknown(string? column, bool? descending, SortColumn expectedColumn, bool expectedDescending) =>
        Assert.Equal(new ContainerSort(expectedColumn, expectedDescending), ContainerSort.Read(column, descending));
}

using Berth.Presentation;
using Berth.State;
using static Berth.Tests.Presentation.ResourceFixtures;

namespace Berth.Tests.Presentation;

public sealed class RowSelectionTests
{
    private static readonly IReadOnlyList<VolumeRow> Rows = VolumeRows.From(
        Pending(StateWith(volumes: [Volume("a"), Volume("b"), Volume("c")]), ResourceKind.Volumes, "c"));

    [Fact]
    public void The_count_is_of_checked_rows_that_are_shown_pending_ones_included()
    {
        Assert.Equal(2, RowSelection.SelectedCount(Rows.Where(r => r.Name != "b"), Keys("a", "b", "c")));
    }

    [Fact]
    public void Checked_keys_are_the_shown_checked_rows_that_can_act()
    {
        Assert.Equal<string>(["a", "b"], RowSelection.Checked(Rows, Keys("a", "b", "c", "gone"), connected: true));
        Assert.Equal<string>(["b"], RowSelection.Checked(Rows, Keys("a", "b"), connected: true, applies: r => r.Name == "b"));
        Assert.Empty(RowSelection.Checked(Rows, Keys("a", "b"), connected: false));
    }

    [Fact]
    public void Confirmed_rows_are_those_still_listed_and_not_pending_in_the_order_asked()
    {
        Assert.Equal(["b", "a"], RowSelection.Confirmed(Rows, ["b", "gone", "c", "a"], connected: true).Select(r => r.Name));
        Assert.Empty(RowSelection.Confirmed(Rows, ["a"], connected: false));
    }

    [Fact]
    public void Name_lines_list_ten_then_sum_up_the_rest()
    {
        Assert.Equal<string>(["a", "b"], RowSelection.NameLines(["a", "b"]));
        var lines = RowSelection.NameLines([.. Enumerable.Range(1, 11).Select(i => $"n{i}")]);
        Assert.Equal(11, lines.Count);
        Assert.Equal("n10", lines[9]);
        Assert.Equal("and 1 more", lines[10]);
    }

    [Fact]
    public void A_list_sort_toggles_like_the_containers_table_and_names_itself_for_screen_readers()
    {
        var byName = new ListSort<VolumeColumn>(VolumeColumn.Name, Descending: false);

        Assert.Equal(byName with { Descending = true }, byName.Toggle(VolumeColumn.Name));
        Assert.Equal(new ListSort<VolumeColumn>(VolumeColumn.Size, false), byName.Toggle(VolumeColumn.Size));
        Assert.Equal("Name, sorted ascending", byName.AccessibleName(VolumeColumn.Name));
        Assert.Equal("Size, not sorted", byName.AccessibleName(VolumeColumn.Size));
        Assert.Equal("Name, sorted descending", byName.Toggle(VolumeColumn.Name).AccessibleName(VolumeColumn.Name));
    }
}

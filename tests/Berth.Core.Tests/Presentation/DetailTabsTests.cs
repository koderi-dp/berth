using Berth.Presentation;

namespace Berth.Tests.Presentation;

/// <summary>Which tabs a container's page shows, in which order, and what they are called.</summary>
public sealed class DetailTabsTests
{
    [Fact]
    public void Shown_puts_exec_before_stats() =>
        Assert.Equal(
            [DetailTab.Logs, DetailTab.Inspect, DetailTab.BindMounts, DetailTab.Exec, DetailTab.Stats],
            DetailTabs.Shown);

    [Fact]
    public void Titles()
    {
        Assert.Equal("Logs", DetailTabs.Title(DetailTab.Logs));
        Assert.Equal("Inspect", DetailTabs.Title(DetailTab.Inspect));
        Assert.Equal("Bind mounts", DetailTabs.Title(DetailTab.BindMounts));
        Assert.Equal("Exec", DetailTabs.Title(DetailTab.Exec));
        Assert.Equal("Stats", DetailTabs.Title(DetailTab.Stats));
    }

    [Fact]
    public void At_out_of_range_is_logs()
    {
        var shown = DetailTabs.Shown;

        Assert.Equal(DetailTab.Stats, DetailTabs.At(shown, 4));
        Assert.Equal(DetailTab.Logs, DetailTabs.At(shown, 5));
        Assert.Equal(DetailTab.Logs, DetailTabs.At(shown, -1));
    }

    [Fact]
    public void IndexOf_a_tab_not_in_the_list_is_zero()
    {
        IReadOnlyList<DetailTab> withoutExec = [DetailTab.Logs, DetailTab.Stats];

        Assert.Equal(0, DetailTabs.IndexOf(withoutExec, DetailTab.Exec));
        Assert.Equal(4, DetailTabs.IndexOf(DetailTabs.Shown, DetailTab.Stats));
    }

    [Fact]
    public void Shown_is_the_same_list_each_time() =>
        Assert.Same(DetailTabs.Shown, DetailTabs.Shown);
}

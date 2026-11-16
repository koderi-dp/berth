using Berth.Presentation;

namespace Berth.Tests.Presentation;

/// <summary>What a container page's back button says, from where the page was opened.</summary>
public sealed class BackTargetTests
{
    [Fact]
    public void Opened_from_the_containers_list_it_goes_back_to_containers() =>
        Assert.Equal("Back to containers", BackTarget.Containers.Label);

    [Fact]
    public void Opened_from_a_volumes_page_it_names_the_volume() =>
        Assert.Equal("Back to volume berth-check-vol", BackTarget.Volume("berth-check-vol").Label);

    [Fact]
    public void Opened_from_the_networks_list_it_goes_back_to_networks() =>
        Assert.Equal("Back to networks", BackTarget.Networks.Label);
}

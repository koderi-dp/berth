using Berth.Engine.Models;
using Berth.State;
using static Berth.Tests.Presentation.ResourceFixtures;

namespace Berth.Tests.State;

public sealed class InUseTests
{
    [Fact]
    public void An_image_is_in_use_when_the_engine_counts_a_container_or_a_listed_container_runs_it()
    {
        ContainerSummary[] containers = [User("web", imageId: "sha256:a")];

        Assert.True(InUse.Image(Image("sha256:a"), containers));
        Assert.True(InUse.Image(Image("sha256:b", containers: 2), containers));
        Assert.False(InUse.Image(Image("sha256:b", containers: null), containers));
        Assert.False(InUse.Image(Image("sha256:b"), []));
    }

    [Fact]
    public void A_volume_is_in_use_when_a_container_of_any_state_mounts_it_as_a_volume()
    {
        ContainerSummary[] containers = [User("web", volumes: ["data"], state: "exited"), User("job", bind: "data")];

        Assert.True(InUse.Volume("data", containers));
        Assert.False(InUse.Volume("logs", containers));
        Assert.False(InUse.IsVolumeMount(new MountDetails("bind", "/data", "/host", "", true, null)));
        Assert.False(InUse.IsVolumeMount(new MountDetails("volume", "", "/x", "", true, "")));
    }

    [Fact]
    public void A_network_is_in_use_when_a_container_of_any_state_is_attached_to_it_by_name()
    {
        ContainerSummary[] containers = [User("web", networks: ["app"], state: "exited")];

        Assert.True(InUse.Network("app", containers));
        Assert.False(InUse.Network("App", containers));
        Assert.False(InUse.Network("other", containers));
    }
}

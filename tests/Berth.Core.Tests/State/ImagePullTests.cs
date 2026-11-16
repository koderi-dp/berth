using Berth.Engine.Models;
using Berth.State;

namespace Berth.Tests.State;

public sealed class ImagePullTests
{
    [Fact]
    public void Lines_update_their_layer_in_the_order_layers_were_first_named()
    {
        var pull = ImagePull.Start("nginx:latest", 1).With(
        [
            new PullProgress("l1", "Pulling fs layer", null, null),
            new PullProgress("l2", "Pulling fs layer", null, null),
            new PullProgress("l2", "Downloading", 5, 50),
            new PullProgress("l1", "Downloading", 1, 10),
        ]);

        Assert.Equal<PullLayer>([new PullLayer("l1", "Downloading", 1, 10), new PullLayer("l2", "Downloading", 5, 50)], pull.Layers);
    }

    [Fact]
    public void A_layer_keeps_its_size_when_later_lines_carry_none()
    {
        var pull = ImagePull.Start("nginx:latest", 1).With(
        [
            new PullProgress("l1", "Downloading", 1, 10),
            new PullProgress("l1", "Download complete", null, null),
            new PullProgress("l1", "Pull complete", null, null),
        ]);

        Assert.Equal<PullLayer>([new PullLayer("l1", "Pull complete", null, 10)], pull.Layers);
    }

    [Fact]
    public void Lines_about_the_whole_pull_are_its_last_status_and_not_layers()
    {
        var pull = ImagePull.Start("nginx:latest", 1).With(
        [
            new PullProgress("latest", "Pulling from library/nginx", null, null),
            new PullProgress("", "Digest: sha256:abc", null, null),
        ]);

        Assert.Empty(pull.Layers);
        Assert.Equal("Digest: sha256:abc", pull.LastStatus);
    }

    [Fact]
    public void Applying_lines_leaves_the_original_unchanged()
    {
        var start = ImagePull.Start("nginx:latest", 1);

        _ = start.With([new PullProgress("l1", "Waiting", null, null)]);

        Assert.Empty(start.Layers);
    }

    [Theory]
    [InlineData("nginx", "nginx:latest")]
    [InlineData(" nginx:1.27 ", "nginx:1.27")]
    [InlineData("host:5000/a/b", "host:5000/a/b:latest")]
    [InlineData("a:1@sha256:abc", "a@sha256:abc")]
    [InlineData("nginx:latest", "nginx:latest")]
    public void A_reference_is_keyed_by_its_name_and_tag_or_digest(string typed, string key)
    {
        Assert.Equal(key, ImagePull.KeyOf(typed));
        Assert.Equal(key, ImagePull.KeyOf(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("nginx:")]
    [InlineData("@sha256:abc")]
    public void Something_that_is_no_reference_has_no_key(string typed)
    {
        Assert.Null(ImagePull.KeyOf(typed));
    }
}

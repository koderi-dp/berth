using Berth.Engine;

namespace Berth.Tests.Engine;

public sealed class ImageReferenceTests
{
    [Theory]
    [InlineData("nginx", "nginx", "latest")]
    [InlineData("nginx:1.29", "nginx", "1.29")]
    [InlineData("  alpine:3.19  ", "alpine", "3.19")]
    [InlineData("library/alpine", "library/alpine", "latest")]
    [InlineData("ghcr.io/owner/app:v2", "ghcr.io/owner/app", "v2")]
    [InlineData("host:5000/a/b:tag", "host:5000/a/b", "tag")]
    [InlineData("host:5000/a/b", "host:5000/a/b", "latest")]
    [InlineData("localhost:5000/app", "localhost:5000/app", "latest")]
    [InlineData("a@sha256:0123abcd", "a", "sha256:0123abcd")]
    [InlineData("a:1.0@sha256:0123abcd", "a", "sha256:0123abcd")]
    [InlineData("host:5000/a@sha256:0123abcd", "host:5000/a", "sha256:0123abcd")]
    [InlineData("host:5000/a:1.0@sha256:0123abcd", "host:5000/a", "sha256:0123abcd")]
    public void A_reference_splits_into_name_and_tag_or_digest(string reference, string name, string tag)
    {
        Assert.Equal(new ImageReference(name, tag), ImageReference.Parse(reference));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(":1.0")]
    [InlineData("@sha256:0123abcd")]
    [InlineData("nginx:")]
    [InlineData("nginx@")]
    public void A_reference_without_a_name_or_with_an_empty_tag_is_refused(string reference)
    {
        Assert.Throws<ArgumentException>(() => ImageReference.Parse(reference));
    }
}

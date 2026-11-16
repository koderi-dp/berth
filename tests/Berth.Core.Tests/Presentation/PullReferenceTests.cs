using Berth.Presentation;

namespace Berth.Tests.Presentation;

/// <summary>What the pull dialog says about the reference typed, and whether Pull can send it.</summary>
public sealed class PullReferenceTests
{
    [Theory]
    [InlineData("nginx")]
    [InlineData("nginx:latest")]
    [InlineData("mcr.microsoft.com/dotnet/sdk:9.0")]
    [InlineData("alpine@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void A_reference_can_be_pulled_and_has_no_error(string typed)
    {
        Assert.Null(PullReference.Error(typed));
        Assert.True(PullReference.CanPull(typed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_typed_has_no_error_but_cannot_be_pulled(string typed)
    {
        Assert.Null(PullReference.Error(typed));
        Assert.False(PullReference.CanPull(typed));
    }

    [Theory]
    [InlineData("my image")]
    [InlineData("nginx ")]
    [InlineData(" nginx")]
    [InlineData("nginx:\tlatest")]
    public void A_reference_with_whitespace_is_refused_before_it_is_sent(string typed)
    {
        Assert.Equal(PullReference.WhitespaceError, PullReference.Error(typed));
        Assert.False(PullReference.CanPull(typed));
    }

    [Theory]
    [InlineData("nginx:")]
    [InlineData(":latest")]
    public void A_reference_that_is_no_image_name_says_what_one_looks_like(string typed)
    {
        Assert.Equal(PullReference.NameError, PullReference.Error(typed));
        Assert.False(PullReference.CanPull(typed));
    }
}

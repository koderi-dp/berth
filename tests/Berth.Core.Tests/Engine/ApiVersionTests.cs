using Berth.Engine;

namespace Berth.Tests.Engine;

public sealed class ApiVersionTests
{
    [Fact]
    public void Parses_and_compares()
    {
        Assert.True(ApiVersion.TryParse("1.44", out var v144));
        Assert.True(ApiVersion.TryParse("1.56", out var v156));
        Assert.True(ApiVersion.TryParse("1.9", out var v19));
        Assert.True(ApiVersion.TryParse("1.10", out var v110));

        Assert.Equal(new ApiVersion(1, 44), v144);
        Assert.True(v144.CompareTo(v156) < 0);
        Assert.True(v19.CompareTo(v110) < 0);
        Assert.True(new ApiVersion(2, 0).CompareTo(v156) > 0);
        Assert.Equal(ApiVersion.Maximum, v156);
        Assert.Equal("1.56", v156.ToString());
        Assert.Equal("1.9", v19.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("x.y")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1.2.3")]
    [InlineData("-1.5")]
    public void Rejects_bad_text(string text)
    {
        Assert.False(ApiVersion.TryParse(text, out _));
    }
}

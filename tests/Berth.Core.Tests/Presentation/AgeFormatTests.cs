using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class AgeFormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(5, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(150, "2 minutes ago")]
    [InlineData(3599, "59 minutes ago")]
    [InlineData(3600, "1 hour ago")]
    [InlineData(7200, "2 hours ago")]
    [InlineData(86399, "23 hours ago")]
    [InlineData(86400, "1 day ago")]
    [InlineData(86400 * 29, "29 days ago")]
    [InlineData(86400 * 30, "1 month ago")]
    [InlineData(86400 * 45, "1 month ago")]
    [InlineData(86400 * 359, "11 months ago")]
    [InlineData(86400 * 360, "1 year ago")]
    [InlineData(86400 * 365, "1 year ago")]
    [InlineData(86400 * 800, "2 years ago")]
    public void Ago(int seconds, string text)
    {
        Assert.Equal(text, AgeFormat.Ago(Now, Now.AddSeconds(-seconds)));
    }

    [Fact]
    public void A_time_in_the_future_is_just_now()
    {
        Assert.Equal("just now", AgeFormat.Ago(Now, Now.AddHours(3)));
    }
}

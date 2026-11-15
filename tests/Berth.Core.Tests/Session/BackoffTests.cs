using Berth.Session;

namespace Berth.Tests.Session;

public sealed class BackoffTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(50, 30)]
    public void Delay_doubles_and_caps(int attempt, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), Backoff.Delay(attempt));
    }
}

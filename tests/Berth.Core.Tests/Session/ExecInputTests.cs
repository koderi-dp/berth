using Berth.Session;

namespace Berth.Tests.Session;

public sealed class ExecInputTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public void The_queue_holds_at_most_two_mebibytes()
    {
        Assert.Equal(2 * MiB, ExecInput.QueueLimit);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2 * MiB)]
    [InlineData(MiB, MiB)]
    [InlineData((2 * MiB) - 1, 1)]
    public void Input_that_fits_under_the_limit_is_taken(long queued, int length)
    {
        Assert.True(ExecInput.Accepts(queued, length));
    }

    [Theory]
    [InlineData(2 * MiB, 1)]
    [InlineData((2 * MiB) - 1, 2)]
    [InlineData(MiB, MiB + 1)]
    [InlineData(0, (2 * MiB) + 1)]
    public void Input_past_the_limit_is_dropped_whole(long queued, int length)
    {
        Assert.False(ExecInput.Accepts(queued, length));
    }
}

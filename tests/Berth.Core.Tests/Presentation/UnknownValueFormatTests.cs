using Berth.Presentation;

namespace Berth.Tests.Presentation;

/// <summary>Volume and network rows carry a created time and a size that can be unknown.</summary>
public sealed class UnknownValueFormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_unknown_age_or_size_reads_unknown()
    {
        Assert.Equal("Unknown", AgeFormat.Ago(Now, (DateTimeOffset?)null));
        Assert.Equal("Unknown", ByteFormat.Bytes((long?)null));
    }

    [Fact]
    public void A_known_age_or_size_reads_as_it_always_has()
    {
        DateTimeOffset? then = Now.AddHours(-3);
        long? size = 2048;

        Assert.Equal("3 hours ago", AgeFormat.Ago(Now, then));
        Assert.Equal("2 KB", ByteFormat.Bytes(size));
    }
}

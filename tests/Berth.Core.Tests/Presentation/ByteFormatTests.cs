using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class ByteFormatTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(999L, "999 B")]
    [InlineData(1000L, "0.98 KB")]
    [InlineData(1023L, "1 KB")]
    [InlineData(1_022_976L, "999 KB")]
    [InlineData(1_024_000L, "0.98 MB")]
    [InlineData(1_047_552L, "1 MB")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1025L, "1 KB")]
    [InlineData(10_240L, "10 KB")]
    [InlineData(102_912L, "101 KB")]
    [InlineData(1_048_575L, "1 MB")]
    [InlineData(1_048_576L, "1 MB")]
    [InlineData(20_971_520L, "20 MB")]
    [InlineData(2_008_000_000L, "1.87 GB")]
    [InlineData(1_099_511_627_776L, "1 TB")]
    public void Bytes_use_binary_units_with_three_significant_digits_at_most(long bytes, string expected)
    {
        Assert.Equal(expected, ByteFormat.Bytes(bytes));
    }

    [Fact]
    public void Bytes_round_to_two_decimals_below_ten_and_one_below_a_hundred()
    {
        Assert.Equal("1.23 KB", ByteFormat.Bytes(1260));
        Assert.Equal("12.3 KB", ByteFormat.Bytes(12_600));
        Assert.Equal("123 KB", ByteFormat.Bytes(126_000));
    }

    [Fact]
    public void Negative_bytes_read_as_zero()
    {
        Assert.Equal("0 B", ByteFormat.Bytes(-5));
    }

    [Theory]
    [InlineData(0d, "0 B/s")]
    [InlineData(512d, "512 B/s")]
    [InlineData(999.4d, "999 B/s")]
    [InlineData(999.5d, "0.98 KB/s")]
    [InlineData(1023.4d, "1 KB/s")]
    [InlineData(1023.6d, "1 KB/s")]
    [InlineData(1024d, "1 KB/s")]
    [InlineData(1536d, "1.5 KB/s")]
    [InlineData(3_145_728d, "3 MB/s")]
    public void Rate_is_bytes_per_second(double rate, string expected)
    {
        Assert.Equal(expected, ByteFormat.Rate(rate));
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void Negative_or_missing_rate_reads_as_zero(double rate)
    {
        Assert.Equal("0 B/s", ByteFormat.Rate(rate));
    }

    [Theory]
    [InlineData(0d, "0.00%")]
    [InlineData(1.4d, "1.40%")]
    [InlineData(12.346d, "12.35%")]
    [InlineData(250d, "250.00%")]
    [InlineData(-3d, "0.00%")]
    [InlineData(double.NaN, "0.00%")]
    public void Percent_has_two_decimals_and_reads_negative_as_zero(double value, string expected)
    {
        Assert.Equal(expected, ByteFormat.Percent(value));
    }
}

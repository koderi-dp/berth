using System.Globalization;

namespace Berth.Presentation;

/// <summary>Formats sizes, transfer rates and percentages for the detail page and the stats figures.</summary>
public static class ByteFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>
    /// A size in binary units ("512 B", "1.5 KB", "20 MB", "1.87 GB") with three significant digits at most: two decimals
    /// below 10, one below 100, none above, trailing zeros dropped; bytes are whole. From 1000 of a unit the next unit is used
    /// ("1000 B" reads "0.98 KB"). A negative size reads "0 B".
    /// </summary>
    public static string Bytes(long bytes) => Format(bytes);

    /// <summary>A rate in bytes per second, as <see cref="Bytes"/> with "/s" ("1.5 KB/s"). Negative or NaN reads "0 B/s".</summary>
    public static string Rate(double bytesPerSecond) => Format(bytesPerSecond) + "/s";

    /// <summary>A percentage with two decimals ("1.40%"). Negative or NaN reads "0.00%".</summary>
    public static string Percent(double value) =>
        (double.IsNaN(value) || value < 0 ? 0 : value).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    private static string Format(double bytes)
    {
        if (double.IsNaN(bytes) || bytes < 0)
        {
            bytes = 0;
        }

        var unit = 0;
        var value = bytes;
        // Four digits would be needed from 1000 up, so 1000 to 1023 of a unit read as a fraction of the next ("0.98 KB").
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var decimals = unit == 0 || value >= 100 ? 0 : value < 10 ? 2 : 1;
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (rounded >= 1000 && unit < Units.Length - 1)
        {
            // 999.6 KB rounds to 1000 KB, which reads as 0.98 MB.
            rounded = Math.Round(value / 1024, 2, MidpointRounding.AwayFromZero);
            unit++;
        }

        return rounded.ToString("0.##", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}

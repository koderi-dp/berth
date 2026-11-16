namespace Berth.Presentation;

/// <summary>Formats how long ago something happened, such as "3 minutes ago".</summary>
public static class AgeFormat
{
    /// <summary>
    /// Under a minute (or in the future) is "just now"; then minutes, hours, days, 30-day months and 365-day years,
    /// each in whole units, singular for one.
    /// </summary>
    public static string Ago(DateTimeOffset now, DateTimeOffset then)
    {
        var seconds = (long)(now - then).TotalSeconds;
        if (seconds < 60)
        {
            return "just now";
        }

        var minutes = seconds / 60;
        if (minutes < 60)
        {
            return Units(minutes, "minute");
        }

        var hours = minutes / 60;
        if (hours < 24)
        {
            return Units(hours, "hour");
        }

        var days = hours / 24;
        if (days < 30)
        {
            return Units(days, "day");
        }

        var months = days / 30;
        if (months < 12)
        {
            return Units(months, "month");
        }

        return Units(Math.Max(1, days / 365), "year");
    }

    /// <summary>As <see cref="Ago(DateTimeOffset, DateTimeOffset)"/>, or <see cref="ByteFormat.Unknown"/> when the time is not known.</summary>
    public static string Ago(DateTimeOffset now, DateTimeOffset? then) => then is { } known ? Ago(now, known) : ByteFormat.Unknown;

    private static string Units(long count, string unit) => count == 1 ? $"1 {unit} ago" : $"{count} {unit}s ago";
}

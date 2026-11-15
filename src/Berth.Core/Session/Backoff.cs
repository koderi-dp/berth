namespace Berth.Session;

/// <summary>Reconnect delays: 1 s for the first attempt, doubling each time, capped at 30 s.</summary>
public static class Backoff
{
    public static readonly TimeSpan Maximum = TimeSpan.FromSeconds(30);

    /// <param name="attempt">How many retries have already been scheduled since the last success, from 0.</param>
    public static TimeSpan Delay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        // 2^5 s is already past the cap; stopping there also keeps the shift from overflowing.
        return attempt >= 5 ? Maximum : TimeSpan.FromSeconds(1 << attempt);
    }
}

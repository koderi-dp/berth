namespace Berth.Presentation;

/// <summary>
/// Counts terminal output sent to the page that xterm has not acknowledged yet, so a flood cannot pile up in the page: above
/// <see cref="High"/> the bridge stops sending (and with it the pump stops reading), and starts again below <see cref="Low"/>.
/// Not thread-safe: the bridge guards it.
/// </summary>
public sealed class TerminalFlow
{
    /// <summary>Stop sending above this many unacknowledged bytes.</summary>
    public const int High = 1024 * 1024;

    /// <summary>Start again below this many.</summary>
    public const int Low = 256 * 1024;

    private long _unacknowledged;

    /// <summary>Whether sending is stopped until enough is acknowledged.</summary>
    public bool Paused { get; private set; }

    /// <summary><paramref name="bytes"/> of output were sent to the page.</summary>
    public void Sent(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        _unacknowledged += bytes;
        if (_unacknowledged > High)
            Paused = true;
    }

    /// <summary>The page has written <paramref name="bytes"/> of output. The count never goes below zero.</summary>
    public void Acked(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        _unacknowledged = Math.Max(0, _unacknowledged - bytes);
        if (Paused && _unacknowledged < Low)
            Paused = false;
    }

    /// <summary>A new page or a new shell: nothing is outstanding.</summary>
    public void Reset()
    {
        _unacknowledged = 0;
        Paused = false;
    }
}

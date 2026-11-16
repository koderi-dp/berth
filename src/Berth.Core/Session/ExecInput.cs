namespace Berth.Session;

/// <summary>
/// How much typed or pasted input waits for a shell. A shell that does not read its input (a program busy printing, or stopped)
/// would otherwise let it pile up without end: repeated pastes, and the terminal's own automatic replies to queries in the output.
/// Past the limit input is dropped, as a full terminal drops it.
/// </summary>
public static class ExecInput
{
    /// <summary>The most bytes waiting to be written to a shell, counting the write under way.</summary>
    public const int QueueLimit = 2 * 1024 * 1024;

    /// <summary>
    /// Whether a message of <paramref name="length"/> bytes is taken while <paramref name="queuedBytes"/> wait: only when all of it
    /// fits under <see cref="QueueLimit"/>; otherwise all of it is dropped (half a paste would be worse than none).
    /// </summary>
    public static bool Accepts(long queuedBytes, int length) => length <= QueueLimit - queuedBytes;
}

namespace Berth.Presentation;

/// <summary>What the terminal does with a piece of output.</summary>
public enum TerminalOutput
{
    /// <summary>An older shell's: not shown.</summary>
    Drop,

    /// <summary>The current shell's: shown.</summary>
    Show,

    /// <summary>A new shell's first: the screen is cleared before it, and the flow count starts again.</summary>
    NewScreen,
}

/// <summary>
/// Which shell's output the terminal shows, by the shell's generation, and the flow count of what it was sent. Generations are a
/// detail session's own and start again at 1 in each: when the terminal follows another session, <see cref="NewSession"/> forgets
/// the old numbering, or the new session's shells would be taken for old ones and dropped. Output carries the session it came from
/// (<see cref="Session"/> when it was attached), so a write the old session's pump had already begun is dropped instead of being
/// taken for a new shell. Not thread-safe: the bridge guards it.
/// </summary>
public sealed class TerminalScreen
{
    /// <summary>The detail session followed: a number that changes with each <see cref="NewSession"/>.</summary>
    public int Session { get; private set; }

    /// <summary>The shell shown; 0 before the first.</summary>
    public int Generation { get; private set; }

    /// <summary>Output sent to the page that xterm has not acknowledged yet.</summary>
    public TerminalFlow Flow { get; } = new();

    /// <summary>The shell of <paramref name="generation"/> is starting: true when it is a new one (clear the screen).</summary>
    public bool Begin(int generation)
    {
        if (generation <= Generation)
            return false;
        StartGeneration(generation);
        return true;
    }

    /// <summary>Output of the shell of <paramref name="generation"/> of <paramref name="session"/> arrived.</summary>
    public TerminalOutput Output(int session, int generation)
    {
        if (session != Session || generation < Generation)
            return TerminalOutput.Drop;
        if (generation == Generation)
            return TerminalOutput.Show;
        StartGeneration(generation);
        return TerminalOutput.NewScreen;
    }

    /// <summary>
    /// The terminal follows another detail session: no shell is shown, nothing is outstanding, and output of the sessions before is
    /// dropped. Returns the new session's number, which its output carries.
    /// </summary>
    public int NewSession()
    {
        Session++;
        Generation = 0;
        Flow.Reset();
        return Session;
    }

    private void StartGeneration(int generation)
    {
        Generation = generation;
        Flow.Reset();
    }
}

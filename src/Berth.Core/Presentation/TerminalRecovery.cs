namespace Berth.Presentation;

/// <summary>A process of the terminal's browser that ended or stopped answering, as the app maps the WebView's own kinds.</summary>
public enum TerminalProcessFailure
{
    /// <summary>The browser process ended: the WebView is unusable.</summary>
    BrowserExited,

    /// <summary>The page's renderer ended (also a frame's): the page is blank until it is loaded again.</summary>
    RendererExited,

    /// <summary>The renderer stopped answering for a while; it may come back by itself.</summary>
    RendererUnresponsive,

    /// <summary>A helper process (GPU, utility, sandbox, ...) ended; the WebView starts it again by itself.</summary>
    HelperExited,
}

/// <summary>What the terminal host does about a failed process.</summary>
public enum TerminalRecoveryStep
{
    /// <summary>Nothing: the WebView recovers by itself, or might.</summary>
    Ignore,

    /// <summary>Load the terminal page again in the same WebView.</summary>
    Reload,

    /// <summary>Close the WebView and start a new one.</summary>
    Recreate,
}

/// <summary>How the terminal host answers a failed process of its browser. After a reload or a new WebView the screen is empty and the
/// shell that was shown on it is ended: the tab says the terminal stopped working and offers to start again.</summary>
public static class TerminalRecovery
{
    public static TerminalRecoveryStep For(TerminalProcessFailure failure) => failure switch
    {
        TerminalProcessFailure.BrowserExited => TerminalRecoveryStep.Recreate,
        TerminalProcessFailure.RendererExited => TerminalRecoveryStep.Reload,
        _ => TerminalRecoveryStep.Ignore,
    };

    /// <summary>Whether <paramref name="step"/> loses what the terminal showed, and with it the shell.</summary>
    public static bool EndsTheShell(TerminalRecoveryStep step) => step != TerminalRecoveryStep.Ignore;
}

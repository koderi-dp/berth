using Berth.Session;

namespace Berth.Presentation;

/// <summary>Where the engine is, as the status bar, the disconnected view and the tray tell it.</summary>
public enum EnginePhase { Running, Connecting, NotReachable, Starting, Stopping, WaitingForStart, Stopped, NeedsAttention }

/// <summary>The colour family of a status: the theme's success, caution or critical fill, or neutral.</summary>
public enum StatusTone { Success, Caution, Critical, Neutral }

/// <summary>What the disconnected view shows.</summary>
/// <param name="Title">The heading.</param>
/// <param name="Message">Why the engine could not be reached, in the words the engine or the socket used; null when there is nothing to say.</param>
/// <param name="Failure">A start or stop command that failed: what failed and its output as it wrote it; null when none did.</param>
/// <param name="Busy">Something is under way (a progress ring); no button acts meanwhile.</param>
/// <param name="CanRetry">Retry is offered.</param>
/// <param name="OfferStart">"Start engine" is offered (the connection has engine control and no command is running).</param>
public sealed record DisconnectedText(string Title, string? Message, string? Failure, bool Busy, bool CanRetry, bool OfferStart)
{
    /// <summary>The dialog an SSH connection that stopped for the user offers (a host key to check, a secret to enter); null otherwise.</summary>
    public SshPrompt? Prompt { get; init; }
}

/// <summary>The engine command a menu offers (the status bar's, the tray's): stop while the engine runs, start otherwise.</summary>
/// <param name="Enabled">False while a command runs.</param>
public sealed record EngineMenu(EngineCommand Command, bool Enabled)
{
    /// <summary>The menu item's label.</summary>
    public string Label => Command == EngineCommand.Start ? "Start engine" : "Stop engine…";
}

/// <summary>What the status bar, the disconnected view and the tray say and offer for each engine state.</summary>
public static class EngineStatus
{
    public const string StopTitle = "Stop the engine";

    public static EnginePhase Phase(EngineState state)
    {
        var control = state.EngineControl;
        if (control.Running is { } running)
            return running == EngineCommand.Start ? EnginePhase.Starting : EnginePhase.Stopping;
        if (state.Status == ConnectionStatus.Connected)
            return EnginePhase.Running;
        if (control.StoppedByBerth)
            return EnginePhase.Stopped;
        if (control.StartWaitsLeft > 0)
            return EnginePhase.WaitingForStart;
        if (state.Status == ConnectionStatus.Disconnected && state.Attention is not null)
            return EnginePhase.NeedsAttention;
        return state.Status == ConnectionStatus.Connecting ? EnginePhase.Connecting : EnginePhase.NotReachable;
    }

    /// <summary>The status bar's and the tray's words for <paramref name="phase"/>.</summary>
    public static string Text(EnginePhase phase) => phase switch
    {
        EnginePhase.Running => "Engine running",
        EnginePhase.Connecting => "Connecting…",
        EnginePhase.Starting => "Starting the engine…",
        EnginePhase.Stopping => "Stopping the engine…",
        EnginePhase.WaitingForStart => "Waiting for the engine to start…",
        EnginePhase.Stopped => "Engine stopped",
        EnginePhase.NeedsAttention => "Needs attention",
        _ => "Engine not reachable",
    };

    public static StatusTone Tone(EnginePhase phase) => phase switch
    {
        EnginePhase.Running => StatusTone.Success,
        EnginePhase.NotReachable => StatusTone.Critical,
        EnginePhase.Stopped => StatusTone.Neutral,
        _ => StatusTone.Caution,
    };

    /// <summary>What the disconnected view shows while there is no connection.</summary>
    public static DisconnectedText Disconnected(EngineState state)
    {
        var control = state.EngineControl;
        var failure = control.Failure?.Message;
        var canStart = control.Available && control.Running is null;
        return Phase(state) switch
        {
            EnginePhase.Starting => new DisconnectedText("Starting the engine", null, null, Busy: true, CanRetry: false, OfferStart: false),
            EnginePhase.Stopping => new DisconnectedText("Stopping the engine", null, null, Busy: true, CanRetry: false, OfferStart: false),
            EnginePhase.WaitingForStart => new DisconnectedText("Waiting for the engine to start", null, failure, Busy: true, CanRetry: false, OfferStart: false),
            // A retry keeps the last error and "Start engine" on screen: a refused connect takes seconds, and the view would flicker.
            EnginePhase.Connecting => new DisconnectedText(
                "Connecting to the engine",
                state.ConnectionError,
                failure,
                Busy: true,
                CanRetry: false,
                OfferStart: canStart && state.ConnectionError is not null),
            EnginePhase.Stopped => new DisconnectedText("The engine was stopped.", null, failure, Busy: false, CanRetry: true, OfferStart: canStart),
            EnginePhase.NeedsAttention => Attention(state),
            _ => new DisconnectedText("Not connected to the engine", state.ConnectionError, failure, Busy: false, CanRetry: true, OfferStart: canStart),
        };
    }

    /// <summary>
    /// An SSH connection that stopped for the user: the dialog's button in place of Retry, except after a changed key (Retry checks again,
    /// it never accepts) and a key file the host refused (nothing to enter; Retry tries the key again, once per click).
    /// </summary>
    private static DisconnectedText Attention(EngineState state)
    {
        var prompt = SshPrompts.For(state.Attention);
        var canRetry = prompt is null or { Kind: SshPromptKind.ChangedHostKey };
        return new DisconnectedText(prompt?.Title ?? "Sign-in refused", state.ConnectionError, null, Busy: false, canRetry, OfferStart: false) { Prompt = prompt };
    }

    /// <summary>The engine command offered, or null when the connection has no engine control.</summary>
    public static EngineMenu? Menu(EngineState state)
    {
        var control = state.EngineControl;
        if (!control.Available)
            return null;
        var command = control.Running ?? (state.Status == ConnectionStatus.Connected ? EngineCommand.Stop : EngineCommand.Start);
        return new EngineMenu(command, Enabled: control.Running is null);
    }

    /// <summary>The question before a stop, naming what will be lost (the containers running now).</summary>
    public static string StopConfirmation(EngineState state)
    {
        var running = state.Containers.Count(container => container.State == "running");
        return running switch
        {
            0 => "Stop the engine? No containers are running.",
            1 => "Stop the engine? 1 running container will stop.",
            _ => $"Stop the engine? {running} running containers will stop.",
        };
    }
}

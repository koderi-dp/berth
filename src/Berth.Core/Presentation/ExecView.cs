using System.Globalization;
using Berth.Session;

namespace Berth.Presentation;

/// <summary>What the Exec tab shows.</summary>
public enum ExecViewKind { Waiting, NotRunning, Paused, Restarting, Ready, Starting, Running, Ended, Failed, Lost }

/// <summary>The one button the Exec tab's line offers, if any.</summary>
public enum ExecButton { None, StartContainer, ResumeContainer, StartAgain, TryAgain, StartSh }

/// <summary>The Exec tab's state, decided from the detail state.</summary>
/// <param name="Text">The line shown; null when the view needs none (Ready, Running).</param>
/// <param name="ShowTerminal">Whether the terminal is on screen (Ready, Starting, Running, Ended, Failed, Lost).</param>
/// <param name="AcceptsInput">Whether typing goes to the shell: Running only.</param>
/// <param name="IsError">Failed: the text is the engine's own.</param>
public sealed record ExecViewModel(ExecViewKind Kind, string? Text, ExecButton Button, bool ShowTerminal, bool AcceptsInput, bool IsError);

/// <summary>The Exec tab's whole behaviour: which state it is in, the line it shows and the button it offers.</summary>
public static class ExecView
{
    /// <summary>What <see cref="ExecButton.StartSh"/> starts.</summary>
    public static readonly System.Collections.Immutable.ImmutableArray<string> ShCommand = ["sh"];

    private static readonly ExecViewModel WaitingView =
        new(ExecViewKind.Waiting, DetailPage.WaitingText, ExecButton.None, ShowTerminal: false, AcceptsInput: false, IsError: false);

    private static readonly ExecViewModel NotRunningView = new(
        ExecViewKind.NotRunning, "Start the container to open a terminal.", ExecButton.StartContainer, false, false, false);

    private static readonly ExecViewModel PausedView = new(
        ExecViewKind.Paused, "Resume the container to open a terminal.", ExecButton.ResumeContainer, false, false, false);

    private static readonly ExecViewModel RestartingView =
        new(ExecViewKind.Restarting, "The container is restarting.", ExecButton.None, false, false, false);

    private static readonly ExecViewModel ReadyView = new(ExecViewKind.Ready, null, ExecButton.None, true, false, false);

    private static readonly ExecViewModel LostView = new(
        ExecViewKind.Lost, "The shell ended when the connection was lost.", ExecButton.StartAgain, true, false, false);

    private static readonly ExecViewModel RunningView = new(ExecViewKind.Running, null, ExecButton.None, true, AcceptsInput: true, false);

    private static readonly ExecViewModel TerminalStoppedView = new(
        ExecViewKind.Failed, ContainerDetailSession.TerminalFailedText, ExecButton.StartAgain, true, false, IsError: true);

    /// <param name="detail">The detail state the page shows.</param>
    /// <param name="waiting"><see cref="DetailPage.Waiting"/>: the session is closed, reopening or its connection is lost.</param>
    /// <param name="startedOnce">The tab has already started a shell since it was shown.</param>
    /// <param name="terminalStopped">
    /// The terminal page was lost (its browser process ended) since the tab last started a shell: that shell was ended with it.
    /// </param>
    /// <remarks>
    /// The container's state wins over the shell's: a shell that ended because the container stopped shows NotRunning. Before the
    /// first read, and for a removed container (the page is leaving), the tab waits.
    /// </remarks>
    public static ExecViewModel For(DetailState detail, bool waiting, bool startedOnce, bool terminalStopped = false)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (waiting || detail.Removed || detail.Details is not { State: var container })
            return WaitingView;
        if (container.Restarting)
            return RestartingView;
        if (!container.Running)
            return NotRunningView;
        if (container.Paused)
            return PausedView;

        if (terminalStopped)
            return TerminalStoppedView;

        var exec = detail.Exec;
        return exec.Status switch
        {
            ExecStatus.Starting => new ExecViewModel(
                ExecViewKind.Starting, $"Starting {string.Join(' ', exec.Command)}…", ExecButton.None, true, false, false),
            ExecStatus.Running => RunningView,
            ExecStatus.Ended => new ExecViewModel(
                ExecViewKind.Ended, EndedText(exec), SuggestsSh(exec) ? ExecButton.StartSh : ExecButton.StartAgain, true, false, false),
            ExecStatus.Failed => new ExecViewModel(ExecViewKind.Failed, exec.Error, ExecButton.TryAgain, true, false, IsError: true),
            _ => startedOnce ? LostView : ReadyView,
        };
    }

    /// <summary>The words on the line's button: Start again (also after a failure), or Start sh where the text suggests sh.</summary>
    public static string ButtonLabel(ExecButton button) => button == ExecButton.StartSh ? "Start sh" : "Start again";

    /// <summary>
    /// Whether the tab still counts a shell as started once it follows a new detail session: only when the old one lost its
    /// connection (then "The shell ended when the connection was lost." is true). A page left and shown again starts a new shell by
    /// itself, as a first visit does.
    /// </summary>
    public static bool StartedOnceAfterNewSession(bool startedOnce, bool previousLost) => startedOnce && previousLost;

    /// <summary>The terminal's size as the toolbar shows it: "120 × 30".</summary>
    public static string SizeText(Engine.Models.TerminalSize size) =>
        string.Create(CultureInfo.InvariantCulture, $"{size.Columns} × {size.Rows}");

    /// <summary>The terminal's size as a screen reader says it: "Terminal size: 120 columns, 30 rows".</summary>
    public static string SizeName(Engine.Models.TerminalSize size) =>
        string.Create(CultureInfo.InvariantCulture, $"Terminal size: {size.Columns} columns, {size.Rows} rows");

    /// <summary>
    /// Whether the tab starts a shell itself: when it is ready, has not started one since it was shown, and a fixed shell is chosen.
    /// A <paramref name="custom"/> command is arbitrary (it could change the container), so it runs only when the user runs it.
    /// </summary>
    public static bool ShouldStartByItself(ExecViewModel view, bool startedOnce, bool custom)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Kind == ExecViewKind.Ready && !startedOnce && !custom;
    }

    /// <summary>
    /// The exit code, and for 126 or 127 before anything was typed (the shell could not be run, most likely because it is not in
    /// the image) a hint that names it and suggests sh, unless it was sh; the button then starts sh (<see cref="ExecButton.StartSh"/>).
    /// </summary>
    private static string EndedText(ExecState exec)
    {
        if (exec.ExitCode is not { } code)
            return "The shell exited.";

        var text = string.Create(CultureInfo.InvariantCulture, $"The shell exited with code {code}.");
        if (!LooksMissing(exec))
            return text;

        var hint = $"{text} {exec.Command[0]} may not be in this image.";
        return SuggestsSh(exec) ? $"{hint} Try sh." : hint;
    }

    /// <summary>126 or 127 before anything was typed: the command could not be run.</summary>
    private static bool LooksMissing(ExecState exec) =>
        exec.ExitCode is 126 or 127 && !exec.HadInput && !exec.Command.IsDefaultOrEmpty;

    private static bool SuggestsSh(ExecState exec) => LooksMissing(exec) && exec.Command[0] != "sh";
}

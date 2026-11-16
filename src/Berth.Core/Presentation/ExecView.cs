using System.Globalization;
using Berth.Session;

namespace Berth.Presentation;

/// <summary>What the Exec tab shows.</summary>
public enum ExecViewKind { Waiting, NotRunning, Paused, Restarting, Ready, Starting, Running, Ended, Failed, Lost }

/// <summary>The one button the Exec tab's line offers, if any.</summary>
public enum ExecButton { None, StartContainer, ResumeContainer, StartAgain, TryAgain }

/// <summary>The Exec tab's state, decided from the detail state.</summary>
/// <param name="Text">The line shown; null when the view needs none (Ready, Running).</param>
/// <param name="ShowTerminal">Whether the terminal is on screen (Ready, Starting, Running, Ended, Failed, Lost).</param>
/// <param name="AcceptsInput">Whether typing goes to the shell: Running only.</param>
/// <param name="IsError">Failed: the text is the engine's own.</param>
public sealed record ExecViewModel(ExecViewKind Kind, string? Text, ExecButton Button, bool ShowTerminal, bool AcceptsInput, bool IsError);

/// <summary>The Exec tab's whole behaviour: which state it is in, the line it shows and the button it offers.</summary>
public static class ExecView
{
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

    /// <param name="detail">The detail state the page shows.</param>
    /// <param name="waiting"><see cref="DetailPage.Waiting"/>: the session is closed, reopening or its connection is lost.</param>
    /// <param name="startedOnce">The tab has already started a shell since it was shown.</param>
    /// <remarks>
    /// The container's state wins over the shell's: a shell that ended because the container stopped shows NotRunning. Before the
    /// first read, and for a removed container (the page is leaving), the tab waits.
    /// </remarks>
    public static ExecViewModel For(DetailState detail, bool waiting, bool startedOnce)
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

        var exec = detail.Exec;
        return exec.Status switch
        {
            ExecStatus.Starting => new ExecViewModel(
                ExecViewKind.Starting, $"Starting {string.Join(' ', exec.Command)}…", ExecButton.None, true, false, false),
            ExecStatus.Running => RunningView,
            ExecStatus.Ended => new ExecViewModel(ExecViewKind.Ended, EndedText(exec), ExecButton.StartAgain, true, false, false),
            ExecStatus.Failed => new ExecViewModel(ExecViewKind.Failed, exec.Error, ExecButton.TryAgain, true, false, IsError: true),
            _ => startedOnce ? LostView : ReadyView,
        };
    }

    /// <summary>Whether the tab starts a shell itself: when it is ready and has not started one since it was shown.</summary>
    public static bool ShouldStartByItself(ExecViewModel view, bool startedOnce)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Kind == ExecViewKind.Ready && !startedOnce;
    }

    /// <summary>
    /// The exit code, and for 126 or 127 before anything was typed (the shell could not be run, most likely because it is not in
    /// the image) a hint that names it and suggests sh, unless it was sh.
    /// </summary>
    private static string EndedText(ExecState exec)
    {
        if (exec.ExitCode is not { } code)
            return "The shell exited.";

        var text = string.Create(CultureInfo.InvariantCulture, $"The shell exited with code {code}.");
        if (code is not (126 or 127) || exec.HadInput || exec.Command.IsDefaultOrEmpty)
            return text;

        var shell = exec.Command[0];
        var hint = $"{text} {shell} may not be in this image.";
        return shell == "sh" ? hint : $"{hint} Try sh.";
    }
}

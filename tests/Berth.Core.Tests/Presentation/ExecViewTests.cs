using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;

namespace Berth.Tests.Presentation;

public sealed class ExecViewTests
{
    private static readonly ImmutableArray<string> Sh = ["sh"];

    private static readonly ImmutableArray<string> Bash = ["bash", "-l"];

    private static DetailState Detail(
        ExecState? exec = null, bool running = true, bool paused = false, bool restarting = false)
    {
        var details = DetailsFixtures.Details();
        return DetailState.Initial("abc123") with
        {
            Details = details with
            {
                State = details.State with { Running = running, Paused = paused, Restarting = restarting },
            },
            Exec = exec ?? ExecState.None,
        };
    }

    private static ExecState Exec(
        ExecStatus status, ImmutableArray<string>? command = null, int? exitCode = null, bool hadInput = false, string? error = null) =>
        ExecState.None with
        {
            Status = status,
            Generation = 1,
            Command = command ?? Sh,
            ExitCode = exitCode,
            HadInput = hadInput,
            Error = error,
        };

    private static void AssertView(
        ExecViewModel view, ExecViewKind kind, string? text, ExecButton button, bool showTerminal, bool acceptsInput = false,
        bool isError = false)
    {
        Assert.Equal(new ExecViewModel(kind, text, button, showTerminal, acceptsInput, isError), view);
    }

    [Fact]
    public void Waiting_for_the_engine() =>
        AssertView(
            ExecView.For(Detail(Exec(ExecStatus.Running)), waiting: true, startedOnce: true),
            ExecViewKind.Waiting, "Waiting for the engine…", ExecButton.None, showTerminal: false);

    [Fact]
    public void A_stopped_container_asks_to_be_started() =>
        AssertView(
            ExecView.For(Detail(running: false), waiting: false, startedOnce: false),
            ExecViewKind.NotRunning, "Start the container to open a terminal.", ExecButton.StartContainer, showTerminal: false);

    [Fact]
    public void A_paused_container_asks_to_be_resumed() =>
        AssertView(
            ExecView.For(Detail(paused: true), waiting: false, startedOnce: false),
            ExecViewKind.Paused, "Resume the container to open a terminal.", ExecButton.ResumeContainer, showTerminal: false);

    [Fact]
    public void A_restarting_container_says_so() =>
        AssertView(
            ExecView.For(Detail(restarting: true), waiting: false, startedOnce: false),
            ExecViewKind.Restarting, "The container is restarting.", ExecButton.None, showTerminal: false);

    [Fact]
    public void A_running_container_without_a_shell_is_ready() =>
        AssertView(
            ExecView.For(Detail(), waiting: false, startedOnce: false),
            ExecViewKind.Ready, null, ExecButton.None, showTerminal: true);

    [Fact]
    public void A_shell_gone_with_the_connection_offers_to_start_again() =>
        AssertView(
            ExecView.For(Detail(), waiting: false, startedOnce: true),
            ExecViewKind.Lost, "The shell ended when the connection was lost.", ExecButton.StartAgain, showTerminal: true);

    [Fact]
    public void Starting_names_the_command() =>
        AssertView(
            ExecView.For(Detail(Exec(ExecStatus.Starting, Bash)), waiting: false, startedOnce: true),
            ExecViewKind.Starting, "Starting bash -l…", ExecButton.None, showTerminal: true);

    [Fact]
    public void Running_takes_input() =>
        AssertView(
            ExecView.For(Detail(Exec(ExecStatus.Running)), waiting: false, startedOnce: true),
            ExecViewKind.Running, null, ExecButton.None, showTerminal: true, acceptsInput: true);

    [Fact]
    public void Ended_gives_the_exit_code()
    {
        AssertView(
            ExecView.For(Detail(Exec(ExecStatus.Ended, exitCode: 0, hadInput: true)), waiting: false, startedOnce: true),
            ExecViewKind.Ended, "The shell exited with code 0.", ExecButton.StartAgain, showTerminal: true);
        AssertView(
            ExecView.For(Detail(Exec(ExecStatus.Ended, exitCode: 127, hadInput: true)), waiting: false, startedOnce: true),
            ExecViewKind.Ended, "The shell exited with code 127.", ExecButton.StartAgain, showTerminal: true);
    }

    [Fact]
    public void Ended_without_a_code_says_it_exited() =>
        AssertView(
            ExecView.For(Detail(Exec(ExecStatus.Ended)), waiting: false, startedOnce: true),
            ExecViewKind.Ended, "The shell exited.", ExecButton.StartAgain, showTerminal: true);

    [Fact]
    public void A_shell_missing_from_the_image_suggests_sh()
    {
        AssertView(
            ExecView.For(Detail(Exec(ExecStatus.Ended, Bash, exitCode: 127)), waiting: false, startedOnce: true),
            ExecViewKind.Ended, "The shell exited with code 127. bash may not be in this image. Try sh.", ExecButton.StartAgain,
            showTerminal: true);
        AssertView(
            ExecView.For(Detail(Exec(ExecStatus.Ended, ["ash"], exitCode: 126)), waiting: false, startedOnce: true),
            ExecViewKind.Ended, "The shell exited with code 126. ash may not be in this image. Try sh.", ExecButton.StartAgain,
            showTerminal: true);
    }

    [Fact]
    public void Failed_shows_the_engines_message() =>
        AssertView(
            ExecView.For(Detail(Exec(ExecStatus.Failed, error: "container abc is not running")), waiting: false, startedOnce: true),
            ExecViewKind.Failed, "container abc is not running", ExecButton.TryAgain, showTerminal: true, isError: true);

    [Fact]
    public void A_stopped_container_wins_over_an_ended_shell()
    {
        var view = ExecView.For(Detail(Exec(ExecStatus.Ended, exitCode: 137), running: false), waiting: false, startedOnce: true);

        Assert.Equal(ExecViewKind.NotRunning, view.Kind);
        Assert.Equal(ExecButton.StartContainer, view.Button);
    }

    [Fact]
    public void Code_127_after_typing_gets_no_hint() =>
        Assert.Equal(
            "The shell exited with code 127.",
            ExecView.For(Detail(Exec(ExecStatus.Ended, Bash, exitCode: 127, hadInput: true)), waiting: false, startedOnce: true).Text);

    [Fact]
    public void Code_127_for_sh_does_not_suggest_sh() =>
        Assert.Equal(
            "The shell exited with code 127. sh may not be in this image.",
            ExecView.For(Detail(Exec(ExecStatus.Ended, Sh, exitCode: 127)), waiting: false, startedOnce: true).Text);

    [Fact]
    public void The_failed_text_is_the_engines_own_unchanged()
    {
        const string Message = "OCI runtime exec failed: exec failed: unable to start container process: exec: \"zsh\": not found\r\n";

        var view = ExecView.For(Detail(Exec(ExecStatus.Failed, error: Message)), waiting: false, startedOnce: true);

        Assert.Same(Message, view.Text);
    }

    [Fact]
    public void The_tab_starts_a_shell_by_itself_once()
    {
        var ready = ExecView.For(Detail(), waiting: false, startedOnce: false);
        var lost = ExecView.For(Detail(), waiting: false, startedOnce: true);

        Assert.True(ExecView.ShouldStartByItself(ready, startedOnce: false));
        Assert.False(ExecView.ShouldStartByItself(ready, startedOnce: true));
        Assert.False(ExecView.ShouldStartByItself(lost, startedOnce: true));
        Assert.False(ExecView.ShouldStartByItself(ExecView.For(Detail(running: false), false, false), startedOnce: false));
        Assert.False(ExecView.ShouldStartByItself(ExecView.For(Detail(), waiting: true, startedOnce: false), startedOnce: false));
    }

    [Fact]
    public void Before_the_first_read_or_after_a_removal_it_waits()
    {
        var unread = DetailState.Initial("abc123");
        var removed = Detail() with { Removed = true };

        Assert.Equal(ExecViewKind.Waiting, ExecView.For(unread, waiting: false, startedOnce: false).Kind);
        Assert.Equal(ExecViewKind.Waiting, ExecView.For(removed, waiting: false, startedOnce: false).Kind);
    }
}

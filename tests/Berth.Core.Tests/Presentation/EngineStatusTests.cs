using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;

namespace Berth.Tests.Presentation;

public sealed class EngineStatusTests
{
    private static readonly EngineState Down = EngineState.Initial with { ConnectionError = "connection refused" };
    private static readonly EngineState Controlled = Down with { EngineControl = EngineControlState.Ready };

    private static ContainerSummary Container(string id, string state) => new(
        id, [$"/{id}"], "nginx:latest", "sha256:1", "nginx", 1_700_000_000, state, "Up", [], new Dictionary<string, string>());

    [Fact]
    public void Each_state_has_one_phase_with_its_text()
    {
        foreach (var (state, phase, text, tone) in Phases)
        {
            Assert.Equal(phase, EngineStatus.Phase(state));
            Assert.Equal(text, EngineStatus.Text(phase));
            Assert.Equal(tone, EngineStatus.Tone(phase));
        }
    }

    private static readonly (EngineState State, EnginePhase Phase, string Text, StatusTone Tone)[] Phases =
    [
        (EngineState.Initial with { Status = ConnectionStatus.Connected }, EnginePhase.Running, "Engine running", StatusTone.Success),
        (EngineState.Initial with { Status = ConnectionStatus.Connecting }, EnginePhase.Connecting, "Connecting…", StatusTone.Caution),
        (Down, EnginePhase.NotReachable, "Engine not reachable", StatusTone.Critical),
        (Controlled with { EngineControl = EngineControlState.Ready with { Running = EngineCommand.Start } }, EnginePhase.Starting, "Starting the engine…", StatusTone.Caution),
        (Controlled with { Status = ConnectionStatus.Connected, EngineControl = EngineControlState.Ready with { Running = EngineCommand.Stop } }, EnginePhase.Stopping, "Stopping the engine…", StatusTone.Caution),
        (Controlled with { EngineControl = EngineControlState.Ready with { StartWaitsLeft = 3 } }, EnginePhase.WaitingForStart, "Waiting for the engine to start…", StatusTone.Caution),
        (Controlled with { Status = ConnectionStatus.Connecting, EngineControl = EngineControlState.Ready with { StartWaitsLeft = 3 } }, EnginePhase.WaitingForStart, "Waiting for the engine to start…", StatusTone.Caution),
        (Controlled with { ConnectionError = null, EngineControl = EngineControlState.Ready with { StoppedByBerth = true } }, EnginePhase.Stopped, "Engine stopped", StatusTone.Neutral),
    ];

    [Fact]
    public void Disconnected_without_engine_control_gives_the_error_and_retry_only()
    {
        var view = EngineStatus.Disconnected(Down);

        Assert.Equal(new DisconnectedText("Not connected to the engine", "connection refused", null, Busy: false, CanRetry: true, OfferStart: false), view);
    }

    [Fact]
    public void Disconnected_with_engine_control_offers_start_engine()
    {
        var view = EngineStatus.Disconnected(Controlled);

        Assert.True(view.OfferStart);
        Assert.True(view.CanRetry);
        Assert.Equal("connection refused", view.Message);
    }

    [Fact]
    public void Stopped_from_berth_says_so_and_offers_start_engine_not_as_an_error()
    {
        var view = EngineStatus.Disconnected(Controlled with { ConnectionError = null, EngineControl = EngineControlState.Ready with { StoppedByBerth = true } });

        Assert.Equal("The engine was stopped.", view.Title);
        Assert.Null(view.Message);
        Assert.True(view.OfferStart);
        Assert.True(view.CanRetry);
        Assert.False(view.Busy);
    }

    [Fact]
    public void Starting_shows_progress_and_offers_nothing()
    {
        var view = EngineStatus.Disconnected(Controlled with { EngineControl = EngineControlState.Ready with { Running = EngineCommand.Start } });

        Assert.Equal(new DisconnectedText("Starting the engine", null, null, Busy: true, CanRetry: false, OfferStart: false), view);
    }

    [Fact]
    public void Waiting_after_a_start_shows_progress()
    {
        var view = EngineStatus.Disconnected(Controlled with { EngineControl = EngineControlState.Ready with { StartWaitsLeft = 10 } });

        Assert.Equal("Waiting for the engine to start", view.Title);
        Assert.True(view.Busy);
        Assert.Null(view.Message);
    }

    [Fact]
    public void A_failed_command_is_shown_with_its_output_as_is_and_start_is_offered_again()
    {
        var failure = new EngineCommandFailure(EngineCommand.Start, EngineCommandFailureKind.ExitCode, 1, "Job for docker.service failed.");
        var view = EngineStatus.Disconnected(Controlled with { EngineControl = EngineControlState.Ready with { Failure = failure } });

        Assert.Equal("The start command failed with exit code 1.\nJob for docker.service failed.", view.Failure);
        Assert.True(view.OfferStart);
    }

    [Fact]
    public void The_engine_menu_offers_stop_while_running_start_while_not_and_nothing_without_control()
    {
        Assert.Equal(new EngineMenu(EngineCommand.Stop, Enabled: true), EngineStatus.Menu(Controlled with { Status = ConnectionStatus.Connected }));
        Assert.Equal(new EngineMenu(EngineCommand.Start, Enabled: true), EngineStatus.Menu(Controlled));
        Assert.Equal(new EngineMenu(EngineCommand.Start, Enabled: false), EngineStatus.Menu(Controlled with { EngineControl = EngineControlState.Ready with { Running = EngineCommand.Start } }));
        Assert.Equal(new EngineMenu(EngineCommand.Stop, Enabled: false), EngineStatus.Menu(Controlled with { Status = ConnectionStatus.Connected, EngineControl = EngineControlState.Ready with { Running = EngineCommand.Stop } }));
        Assert.Null(EngineStatus.Menu(Down with { Status = ConnectionStatus.Connected }));
    }

    [Fact]
    public void The_stop_confirmation_counts_the_running_containers()
    {
        var state = Controlled with
        {
            Status = ConnectionStatus.Connected,
            Containers = [Container("a", "running"), Container("b", "exited"), Container("c", "running")],
        };

        Assert.Equal("Stop the engine? 2 running containers will stop.", EngineStatus.StopConfirmation(state));
        Assert.Equal("Stop the engine? 1 running container will stop.", EngineStatus.StopConfirmation(state with { Containers = [Container("a", "running")] }));
        Assert.Equal("Stop the engine? No containers are running.", EngineStatus.StopConfirmation(state with { Containers = [] }));
    }
}

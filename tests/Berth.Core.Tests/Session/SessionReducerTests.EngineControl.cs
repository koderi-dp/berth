using Berth.Session;

namespace Berth.Tests.Session;

public sealed partial class SessionReducerTests
{
    private static EngineState WithControl(EngineState state) => state with { EngineControl = EngineControlState.Ready };

    private static EngineState Disconnected(string error = "connection refused")
    {
        var (connecting, _) = Reduce(WithControl(EngineState.Initial), new ConnectRequested());
        var (failed, _) = Reduce(connecting, new ConnectFailed(error));
        return failed;
    }

    [Fact]
    public void Engine_commands_are_dropped_when_the_connection_has_no_engine_control()
    {
        var state = Connected();

        var (next, effects) = Reduce(state, new EngineCommandRequested(EngineCommand.Stop));

        Assert.Same(state, next);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_start_request_runs_the_start_command_and_clears_an_earlier_failure()
    {
        var failure = new EngineCommandFailure(EngineCommand.Start, EngineCommandFailureKind.ExitCode, 1, "no");
        var state = Disconnected() with { EngineControl = EngineControlState.Ready with { Failure = failure } };

        var (next, effects) = Reduce(state, new EngineCommandRequested(EngineCommand.Start));

        Assert.Equal(EngineCommand.Start, next.EngineControl.Running);
        Assert.Null(next.EngineControl.Failure);
        Assert.Equal([new RunEngineCommandEffect(EngineCommand.Start)], effects);
    }

    [Fact]
    public void A_second_request_while_one_runs_is_dropped()
    {
        var (running, _) = Reduce(WithControl(Connected()), new EngineCommandRequested(EngineCommand.Stop));

        foreach (var command in new[] { EngineCommand.Stop, EngineCommand.Start })
        {
            var (next, effects) = Reduce(running, new EngineCommandRequested(command));
            Assert.Same(running, next);
            Assert.Empty(effects);
        }
    }

    [Fact]
    public void A_successful_start_reconnects_at_once()
    {
        var (starting, _) = Reduce(Disconnected(), new EngineCommandRequested(EngineCommand.Start));

        var (next, effects) = Reduce(starting, new EngineCommandExited(EngineCommand.Start, 0, ""));

        Assert.Null(next.EngineControl.Running);
        Assert.Equal(ConnectionStatus.Connecting, next.Status);
        Assert.Equal(SessionReducer.StartWaits, next.EngineControl.StartWaitsLeft);
        Assert.Equal([new ConnectEffect()], effects);
    }

    [Fact]
    public void After_a_start_a_refused_connect_retries_every_second_for_a_bounded_time()
    {
        var (starting, _) = Reduce(Disconnected(), new EngineCommandRequested(EngineCommand.Start));
        var (state, _) = Reduce(starting, new EngineCommandExited(EngineCommand.Start, 0, ""));

        for (var wait = SessionReducer.StartWaits; wait > 0; wait--)
        {
            var (failed, effects) = Reduce(state, new ConnectFailed("connection refused"));
            Assert.Equal(wait - 1, failed.EngineControl.StartWaitsLeft);
            Assert.Equal(0, failed.RetryAttempt);
            Assert.Equal([new ScheduleRetryEffect(SessionReducer.StartWaitDelay)], effects);
            (state, _) = Reduce(failed, new RetryDue());
        }

        // Then the usual back-off.
        var (last, lastEffects) = Reduce(state, new ConnectFailed("connection refused"));
        Assert.Equal(1, last.RetryAttempt);
        Assert.Equal([new ScheduleRetryEffect(Backoff.Delay(0))], lastEffects);
    }

    [Fact]
    public void A_connect_ends_the_wait_after_a_start()
    {
        var (starting, _) = Reduce(Disconnected(), new EngineCommandRequested(EngineCommand.Start));
        var (connecting, _) = Reduce(starting, new EngineCommandExited(EngineCommand.Start, 0, ""));

        var (connected, _) = Reduce(connecting, new ConnectSucceeded(Version, Info));

        Assert.Equal(0, connected.EngineControl.StartWaitsLeft);
    }

    [Fact]
    public void A_successful_start_while_connected_changes_nothing_else()
    {
        var (starting, _) = Reduce(WithControl(Connected()), new EngineCommandRequested(EngineCommand.Start));

        var (next, effects) = Reduce(starting, new EngineCommandExited(EngineCommand.Start, 0, ""));

        Assert.Equal(ConnectionStatus.Connected, next.Status);
        Assert.Null(next.EngineControl.Running);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_successful_stop_disconnects_without_an_error_and_pauses_reconnecting()
    {
        var (stopping, _) = Reduce(WithControl(Connected()), new EngineCommandRequested(EngineCommand.Stop));

        var (stopped, effects) = Reduce(stopping, new EngineCommandExited(EngineCommand.Stop, 0, ""));

        Assert.Equal(ConnectionStatus.Disconnected, stopped.Status);
        Assert.Null(stopped.ConnectionError);
        Assert.True(stopped.EngineControl.StoppedByBerth);
        Assert.Null(stopped.EngineControl.Running);
        Assert.Equal([new StopStreamsEffect()], effects);
    }

    [Fact]
    public void A_stop_that_lost_the_connection_first_still_ends_stopped_without_an_error_and_no_retry_runs()
    {
        var (stopping, _) = Reduce(WithControl(Connected()), new EngineCommandRequested(EngineCommand.Stop));
        var (lost, _) = Reduce(stopping, new ConnectionLost("The engine closed the connection."));
        var (retrying, _) = Reduce(lost, new RetryDue());

        var (stopped, _) = Reduce(retrying, new EngineCommandExited(EngineCommand.Stop, 0, ""));
        var (afterRetry, retryEffects) = Reduce(stopped, new RetryDue());

        Assert.Equal(ConnectionStatus.Disconnected, stopped.Status);
        Assert.Null(stopped.ConnectionError);
        Assert.Same(stopped, afterRetry);
        Assert.Empty(retryEffects);
    }

    [Fact]
    public void Retry_after_a_stop_connects_again_and_ends_the_pause()
    {
        var (stopping, _) = Reduce(WithControl(Connected()), new EngineCommandRequested(EngineCommand.Stop));
        var (stopped, _) = Reduce(stopping, new EngineCommandExited(EngineCommand.Stop, 0, ""));

        var (next, effects) = Reduce(stopped, new ConnectRequested());

        Assert.False(next.EngineControl.StoppedByBerth);
        Assert.Equal(ConnectionStatus.Connecting, next.Status);
        Assert.Equal([new ConnectEffect()], effects);
    }

    [Fact]
    public void Start_after_a_stop_ends_the_pause_and_reconnects()
    {
        var (stopping, _) = Reduce(WithControl(Connected()), new EngineCommandRequested(EngineCommand.Stop));
        var (stopped, _) = Reduce(stopping, new EngineCommandExited(EngineCommand.Stop, 0, ""));
        var (starting, _) = Reduce(stopped, new EngineCommandRequested(EngineCommand.Start));

        var (next, effects) = Reduce(starting, new EngineCommandExited(EngineCommand.Start, 0, ""));

        Assert.False(next.EngineControl.StoppedByBerth);
        Assert.Equal(ConnectionStatus.Connecting, next.Status);
        Assert.Equal([new ConnectEffect()], effects);
    }

    [Fact]
    public void A_non_zero_exit_is_a_failure_with_the_output_as_is()
    {
        var (starting, _) = Reduce(Disconnected(), new EngineCommandRequested(EngineCommand.Start));

        var (next, effects) = Reduce(starting, new EngineCommandExited(EngineCommand.Start, 5, "Job for docker.service failed.\nSee \"systemctl status\"."));

        Assert.Equal(new EngineCommandFailure(EngineCommand.Start, EngineCommandFailureKind.ExitCode, 5, "Job for docker.service failed.\nSee \"systemctl status\"."), next.EngineControl.Failure);
        Assert.Null(next.EngineControl.Running);
        Assert.Equal(ConnectionStatus.Disconnected, next.Status);
        Assert.Null(next.ActionError);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_failure_while_connected_also_shows_in_the_error_bar()
    {
        var (stopping, _) = Reduce(WithControl(Connected()), new EngineCommandRequested(EngineCommand.Stop));

        var (next, _) = Reduce(stopping, new EngineCommandExited(EngineCommand.Stop, 1, "Access denied"));

        Assert.Equal(ConnectionStatus.Connected, next.Status);
        Assert.False(next.EngineControl.StoppedByBerth);
        Assert.Equal(next.EngineControl.Failure!.Message, next.ActionError);
        Assert.Equal("The stop command failed with exit code 1.\nAccess denied", next.ActionError);
    }

    [Fact]
    public void A_command_that_could_not_be_run_is_a_failure_with_the_reason()
    {
        var (starting, _) = Reduce(Disconnected(), new EngineCommandRequested(EngineCommand.Start));

        var (next, _) = Reduce(starting, new EngineCommandNotStarted(EngineCommand.Start, "The system cannot find the file specified."));

        Assert.Equal(EngineCommandFailureKind.NotStarted, next.EngineControl.Failure!.Kind);
        Assert.Equal("The start command could not be run.\nThe system cannot find the file specified.", next.EngineControl.Failure.Message);
        Assert.Null(next.EngineControl.Running);
    }

    [Fact]
    public void A_command_that_timed_out_is_a_failure()
    {
        var (stopping, _) = Reduce(WithControl(Connected()), new EngineCommandRequested(EngineCommand.Stop));

        var (next, _) = Reduce(stopping, new EngineCommandTimedOut(EngineCommand.Stop));

        Assert.Equal(EngineCommandFailureKind.TimedOut, next.EngineControl.Failure!.Kind);
        Assert.Equal("The stop command did not finish within 60 seconds and was ended.", next.EngineControl.Failure.Message);
        Assert.False(next.EngineControl.StoppedByBerth);
    }

    [Fact]
    public void A_result_for_a_command_not_running_is_dropped()
    {
        var state = WithControl(Connected());

        foreach (var e in new SessionEvent[]
                 {
                     new EngineCommandExited(EngineCommand.Stop, 0, ""),
                     new EngineCommandNotStarted(EngineCommand.Stop, "x"),
                     new EngineCommandTimedOut(EngineCommand.Stop),
                 })
        {
            var (next, effects) = Reduce(state, e);
            Assert.Same(state, next);
            Assert.Empty(effects);
        }
    }
}

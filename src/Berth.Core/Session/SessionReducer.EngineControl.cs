using System.Collections.Immutable;

namespace Berth.Session;

/// <summary>Starting and stopping the engine with the connection's commands.</summary>
public static partial class SessionReducer
{
    /// <summary>How long a start or stop command may run before it is ended.</summary>
    public static readonly TimeSpan EngineCommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary>After a start, how many quick reconnects are made before the usual back-off: the engine needs a few seconds.</summary>
    public const int StartWaits = 30;

    /// <summary>The delay between the quick reconnects after a start.</summary>
    public static readonly TimeSpan StartWaitDelay = TimeSpan.FromSeconds(1);

    /// <summary>The engine control events; null for any other event.</summary>
    private static (EngineState, ImmutableArray<SessionEffect>)? ReduceEngineControl(EngineState state, SessionEvent e)
    {
        var control = state.EngineControl;
        switch (e)
        {
            case EngineCommandRequested request:
                if (!control.Available || control.Running is not null)
                    return (state, None);
                return (
                    state with { EngineControl = control with { Running = request.Command, Failure = null } },
                    [new RunEngineCommandEffect(request.Command)]);

            case EngineCommandExited exited when control.Running == exited.Command:
                if (exited.ExitCode != 0)
                    return Failed(state, new EngineCommandFailure(exited.Command, EngineCommandFailureKind.ExitCode, exited.ExitCode, exited.Output));
                return exited.Command == EngineCommand.Start ? Started(state) : Stopped(state);

            case EngineCommandNotStarted notStarted when control.Running == notStarted.Command:
                return Failed(state, new EngineCommandFailure(notStarted.Command, EngineCommandFailureKind.NotStarted, 0, notStarted.Message));

            case EngineCommandTimedOut timedOut when control.Running == timedOut.Command:
                return Failed(state, new EngineCommandFailure(timedOut.Command, EngineCommandFailureKind.TimedOut, 0, ""));

            case EngineCommandExited or EngineCommandNotStarted or EngineCommandTimedOut:
                return (state, None);

            default:
                return null;
        }
    }

    /// <summary>
    /// The failure is kept for the disconnected view and the status bar; while connected the error bar shows it too, since the list is
    /// on screen instead.
    /// </summary>
    private static (EngineState, ImmutableArray<SessionEffect>) Failed(EngineState state, EngineCommandFailure failure) => (
        state with
        {
            EngineControl = state.EngineControl with { Running = null, Failure = failure },
            ActionError = state.Status == ConnectionStatus.Connected ? failure.Message : state.ActionError,
        },
        None);

    /// <summary>The engine was started: connect now (the pause after a stop ends), then retry quickly for a while.</summary>
    private static (EngineState, ImmutableArray<SessionEffect>) Started(EngineState state)
    {
        var control = state.EngineControl with { Running = null, StoppedByBerth = false, StartWaitsLeft = StartWaits };
        if (state.Status == ConnectionStatus.Connected)
            return (state with { EngineControl = control with { StartWaitsLeft = 0 } }, None);
        return Reduce(state with { EngineControl = control, ConnectionError = null }, new ConnectRequested());
    }

    /// <summary>
    /// The engine was stopped on purpose: disconnected with no error and no retry, keeping the last lists on screen, until Start engine
    /// or Retry. A connect or stream still under way is retired, so its failure never shows.
    /// </summary>
    private static (EngineState, ImmutableArray<SessionEffect>) Stopped(EngineState state) => (
        ForgetUnderWay(state, startingOver: false) with
        {
            Status = ConnectionStatus.Disconnected,
            ConnectionError = null,
            Pending = state.Pending.Clear(),
            RetryAttempt = 0,
            LoadGeneration = state.LoadGeneration + 1,
            ReloadScheduled = false,
            Stats = state.Stats.Clear(),
            EngineControl = state.EngineControl with { Running = null, StoppedByBerth = true, StartWaitsLeft = 0 },
        },
        [new StopStreamsEffect()]);
}

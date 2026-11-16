using System.Collections.Immutable;

namespace Berth.Session;

/// <summary>The Exec tab's shell: only its status lives in the state, and only these events change it.</summary>
public static partial class DetailReducer
{
    private static (DetailState, ImmutableArray<DetailEffect>) ReduceExec(DetailState state, DetailEvent e)
    {
        var exec = state.Exec;
        switch (e)
        {
            case ExecStartRequested start when CanStartExec(state, start):
            {
                var generation = exec.Generation + 1;
                var next = new ExecState(ExecStatus.Starting, generation, start.Command, start.Size, null, null, false, null)
                {
                    StartedSize = start.Size,
                };
                return (state with { Exec = next }, [new StartExecEffect(generation, start.Command, start.Size)]);
            }

            case ExecStarted started when started.Generation == exec.Generation && exec.Status == ExecStatus.Starting:
            {
                var next = exec with { Status = ExecStatus.Running, ExecId = started.ExecId };
                return exec.Size == exec.StartedSize
                    ? (state with { Exec = next }, None)
                    : (state with { Exec = next }, [new ResizeExecEffect(started.ExecId, exec.Size)]);
            }

            case ExecResizeRequested resize when resize.Size.IsValid && resize.Size != exec.Size:
            {
                var next = state with { Exec = exec with { Size = resize.Size } };
                return exec is { Status: ExecStatus.Running, ExecId: { } id }
                    ? (next, [new ResizeExecEffect(id, resize.Size)])
                    : (next, None);
            }

            case ExecEnded ended when ended.Generation == exec.Generation && exec.IsActive:
                return (state with
                {
                    Exec = exec with { Status = ExecStatus.Ended, ExecId = null, ExitCode = ended.ExitCode, HadInput = ended.HadInput },
                }, None);

            case ExecFailed failed when failed.Generation == exec.Generation && exec.IsActive:
                return (
                    state with { Exec = exec with { Status = ExecStatus.Failed, ExecId = null, Error = failed.Message } },
                    FailedShellNeedsCleanUp(state) ? [new CleanUpExecEffect(exec.Generation)] : None);

            case ExecStopRequested when exec.IsActive:
                return (
                    state with { Exec = ResetExec(exec) },
                    ShellNeedsCleanUp(state) ? [new StopExecEffect(), new CleanUpExecEffect(exec.Generation)] : [new StopExecEffect()]);

            case ExecStopRequested when exec.Status is ExecStatus.Ended or ExecStatus.Failed:
                return (state with { Exec = Cleared(exec) }, None);

            default:
                return (state, None);
        }
    }

    /// <summary>
    /// A shell opens only in a container the last read saw running (not paused, not waiting to restart), on a live connection,
    /// with a command and a size the engine takes, and while no other shell is starting or running.
    /// </summary>
    private static bool CanStartExec(DetailState state, ExecStartRequested start) =>
        !state.ConnectionLost
        && !state.Removed
        && state.Details?.State is { Running: true, Restarting: false, Paused: false }
        && !start.Command.IsDefaultOrEmpty
        && start.Size.IsValid
        && !state.Exec.IsActive;

    /// <summary>
    /// Whether ending the shell now on purpose needs <see cref="ExecCleanUp"/> after it, because the engine keeps a TTY shell after
    /// its stream closes: a shell was asked for and has not ended or failed by itself (a starting one counts: its create or start may
    /// already have reached the engine), and the clean-up can run, in a container the last read saw running (not paused, not
    /// restarting, not removed) on a connection that is still there. A stopped or restarting container has no processes left; a
    /// paused one refuses an exec.
    /// </summary>
    public static bool ShellNeedsCleanUp(DetailState state) =>
        state.Exec.IsActive
        && !state.ConnectionLost
        && !state.Removed
        && state.Details?.State is { Running: true, Paused: false, Restarting: false };

    /// <summary>
    /// Whether a shell that failed (the terminal page died, the stream broke) needs <see cref="ExecCleanUp"/>: the engine keeps it as
    /// it keeps one ended on purpose, so the same rule holds, but only once it ran. A failure while starting is the engine refusing
    /// the create or the start, which left no process behind.
    /// </summary>
    private static bool FailedShellNeedsCleanUp(DetailState state) =>
        state.Exec.Status == ExecStatus.Running && ShellNeedsCleanUp(state);

    /// <summary>No shell: what the old one still sends is dropped (its generation is passed).</summary>
    private static ExecState ResetExec(ExecState exec) => Cleared(exec) with { Generation = exec.Generation + 1 };

    private static ExecState Cleared(ExecState exec) =>
        exec with { Status = ExecStatus.NotStarted, ExecId = null, ExitCode = null, HadInput = false, Error = null };

    /// <summary>
    /// The connection was lost or the container removed: the shell is gone. Only the removed case asks the runtime to stop it (a lost
    /// connection has already stopped every call), and only when one was starting or running.
    /// </summary>
    private static (ExecState Exec, ImmutableArray<DetailEffect> Effects) EndExec(ExecState exec, bool stop) =>
        (ResetExec(exec), stop && exec.IsActive ? [new StopExecEffect()] : None);
}

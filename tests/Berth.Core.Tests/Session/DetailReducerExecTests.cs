using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Session;
using Berth.State;

namespace Berth.Tests.Session;

public sealed class DetailReducerExecTests
{
    private static readonly ImmutableArray<string> Sh = ["sh"];

    private static readonly TerminalSize Size = new(80, 24);

    private static readonly TerminalSize Wide = new(120, 30);

    private static (DetailState State, DetailEffect[] Effects) Reduce(DetailState state, DetailEvent e)
    {
        var (next, effects) = DetailReducer.Reduce(state, e);
        return (next, effects.ToArray());
    }

    private static ContainerInspection Inspection(bool running = true, bool paused = false, bool restarting = false)
    {
        var inspection = FakeEngineApi.Inspection("c", running);
        return inspection with
        {
            Details = inspection.Details with
            {
                State = inspection.Details.State with { Paused = paused, Restarting = restarting },
            },
        };
    }

    /// <summary>Opened on container c and read once, with its streams started.</summary>
    private static DetailState Loaded(ContainerInspection? inspection = null)
    {
        var (opened, _) = Reduce(DetailState.Initial("c"), new DetailOpenRequested());
        var (loaded, _) = Reduce(opened, new InspectLoaded(opened.InspectGeneration, inspection ?? Inspection()));
        return loaded;
    }

    private static DetailState Starting(TerminalSize? size = null) =>
        Reduce(Loaded(), new ExecStartRequested(Sh, size ?? Size)).State;

    private static DetailState Running(TerminalSize? size = null)
    {
        var starting = Starting(size);
        return Reduce(starting, new ExecStarted(starting.Exec.Generation, "exec-1")).State;
    }

    [Fact]
    public void Start_asks_for_a_shell_in_a_running_container()
    {
        var (state, effects) = Reduce(Loaded(), new ExecStartRequested(Sh, Size));

        Assert.Equal(ExecStatus.Starting, state.Exec.Status);
        Assert.Equal(1, state.Exec.Generation);
        Assert.Equal(Sh, state.Exec.Command);
        Assert.Equal(Size, state.Exec.Size);
        Assert.Equal(Size, state.Exec.StartedSize);
        Assert.Equal([new StartExecEffect(1, Sh, Size)], effects);
    }

    [Fact]
    public void Start_is_ignored_while_one_is_starting_or_running()
    {
        foreach (var before in new[] { Starting(), Running() })
        {
            var (state, effects) = Reduce(before, new ExecStartRequested(Sh, Size));

            Assert.Same(before, state);
            Assert.Empty(effects);
        }
    }

    [Fact]
    public void Start_is_ignored_for_a_stopped_paused_or_restarting_container()
    {
        var removed = Reduce(Loaded(), new InspectFailed(1, "No such container: c", NotFound: true)).State;
        var lost = Reduce(Loaded(), new DetailConnectionLost()).State;
        var unread = Reduce(DetailState.Initial("c"), new DetailOpenRequested()).State;
        foreach (var before in new[]
                 {
                     Loaded(Inspection(running: false)), Loaded(Inspection(paused: true)), Loaded(Inspection(restarting: true)),
                     removed, lost, unread,
                 })
        {
            var (state, effects) = Reduce(before, new ExecStartRequested(Sh, Size));

            Assert.Same(before, state);
            Assert.Empty(effects);
        }
    }

    [Fact]
    public void Start_is_ignored_for_an_empty_command_or_an_invalid_size()
    {
        var before = Loaded();
        foreach (var e in new[]
                 {
                     new ExecStartRequested([], Size), new ExecStartRequested(default, Size),
                     new ExecStartRequested(Sh, new TerminalSize(0, 24)), new ExecStartRequested(Sh, new TerminalSize(80, 0)),
                 })
        {
            var (state, effects) = Reduce(before, e);

            Assert.Same(before, state);
            Assert.Empty(effects);
        }
    }

    [Fact]
    public void Started_makes_it_running()
    {
        var (state, effects) = Reduce(Starting(), new ExecStarted(1, "exec-1"));

        Assert.Equal(ExecStatus.Running, state.Exec.Status);
        Assert.Equal("exec-1", state.Exec.ExecId);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_resize_while_starting_is_sent_once_it_runs()
    {
        var (resized, whileStarting) = Reduce(Starting(), new ExecResizeRequested(Wide));
        var (state, effects) = Reduce(resized, new ExecStarted(1, "exec-1"));

        Assert.Empty(whileStarting);
        Assert.Equal(Wide, resized.Exec.Size);
        Assert.Equal(ExecStatus.Running, state.Exec.Status);
        Assert.Equal([new ResizeExecEffect("exec-1", Wide)], effects);
    }

    [Fact]
    public void A_resize_to_the_same_size_sends_nothing()
    {
        var before = Running();

        var (state, effects) = Reduce(before, new ExecResizeRequested(new TerminalSize(80, 24)));

        Assert.Same(before, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_resize_to_zero_columns_is_ignored()
    {
        var before = Running();

        var (state, effects) = Reduce(before, new ExecResizeRequested(new TerminalSize(0, 30)));

        Assert.Same(before, state);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_resize_while_running_is_sent()
    {
        var (state, effects) = Reduce(Running(), new ExecResizeRequested(Wide));

        Assert.Equal(Wide, state.Exec.Size);
        Assert.Equal([new ResizeExecEffect("exec-1", Wide)], effects);
    }

    [Fact]
    public void Ended_keeps_the_exit_code_and_whether_anything_was_typed()
    {
        var (state, effects) = Reduce(Running(), new ExecEnded(1, 3, HadInput: true));

        Assert.Equal(ExecStatus.Ended, state.Exec.Status);
        Assert.Equal(3, state.Exec.ExitCode);
        Assert.True(state.Exec.HadInput);
        Assert.Empty(effects);
    }

    [Fact]
    public void Ended_without_a_code_is_still_ended()
    {
        var (state, _) = Reduce(Running(), new ExecEnded(1, null, HadInput: false));

        Assert.Equal(ExecStatus.Ended, state.Exec.Status);
        Assert.Null(state.Exec.ExitCode);
        Assert.False(state.Exec.HadInput);
    }

    [Fact]
    public void Failed_keeps_the_engines_message()
    {
        const string Message = "container 1234 is not running";

        var (state, effects) = Reduce(Starting(), new ExecFailed(1, Message));

        Assert.Equal(ExecStatus.Failed, state.Exec.Status);
        Assert.Equal(Message, state.Exec.Error);
        Assert.Empty(effects);
    }

    [Fact]
    public void A_running_shell_that_failed_is_cleaned_up_like_one_ended_on_purpose()
    {
        // The terminal page died or the stream broke: the engine still keeps the shell and what it started.
        var (state, effects) = Reduce(Running(), new ExecFailed(1, "broke"));

        Assert.Equal(ExecStatus.Failed, state.Exec.Status);
        Assert.Equal([new CleanUpExecEffect(1)], effects);
    }

    [Fact]
    public void A_failed_shell_is_not_cleaned_up_where_a_stop_would_not_be()
    {
        var running = Running();
        var details = running.Details!;
        DetailState With(Func<ContainerStateDetails, ContainerStateDetails> change) => running with { Details = details with { State = change(details.State) } };

        foreach (var state in new[]
                 {
                     With(s => s with { Running = false }),
                     With(s => s with { Paused = true }),
                     With(s => s with { Restarting = true }),
                     running with { ConnectionLost = true },
                     running with { Removed = true },
                 })
        {
            Assert.Empty(Reduce(state, new ExecFailed(1, "broke")).Effects);
        }
    }

    [Fact]
    public void A_shell_that_failed_before_it_ran_has_nothing_to_clean_up()
    {
        // The create or start was refused: no process was started.
        Assert.Empty(Reduce(Starting(), new ExecFailed(1, "container c is paused")).Effects);
    }

    [Fact]
    public void Stop_drops_what_the_old_shell_still_sends()
    {
        var (stopped, effects) = Reduce(Running(), new ExecStopRequested());

        Assert.Equal(ExecStatus.NotStarted, stopped.Exec.Status);
        Assert.Equal(2, stopped.Exec.Generation);
        Assert.Equal([new StopExecEffect(), new CleanUpExecEffect(1)], effects);

        foreach (var late in new DetailEvent[] { new ExecEnded(1, 0, false), new ExecFailed(1, "gone"), new ExecStarted(1, "exec-1") })
        {
            var (state, more) = Reduce(stopped, late);
            Assert.Same(stopped, state);
            Assert.Empty(more);
        }
    }

    [Fact]
    public void Stopping_a_starting_shell_cleans_up_after_it_once_its_start_has_returned()
    {
        // The create or start may already have reached the engine when the stop cancels it; the shell may exist only once it returns.
        var (_, effects) = Reduce(Starting(), new ExecStopRequested());

        Assert.Equal([new StopExecEffect(), new CleanUpExecEffect(1, AfterStart: true)], effects);
    }

    [Fact]
    public void A_shell_is_cleaned_up_only_while_the_container_runs_on_a_live_connection()
    {
        var running = Running();
        Assert.True(DetailReducer.ShellNeedsCleanUp(running));
        Assert.True(DetailReducer.ShellNeedsCleanUp(Starting()));

        var details = running.Details!;
        DetailState WithState(bool run, bool paused, bool restarting) =>
            running with { Details = details with { State = details.State with { Running = run, Paused = paused, Restarting = restarting } } };

        Assert.False(DetailReducer.ShellNeedsCleanUp(WithState(run: false, paused: false, restarting: false)));
        Assert.False(DetailReducer.ShellNeedsCleanUp(WithState(run: true, paused: true, restarting: false)));
        Assert.False(DetailReducer.ShellNeedsCleanUp(WithState(run: true, paused: false, restarting: true)));
        Assert.False(DetailReducer.ShellNeedsCleanUp(running with { ConnectionLost = true }));
        Assert.False(DetailReducer.ShellNeedsCleanUp(running with { Removed = true }));
        Assert.False(DetailReducer.ShellNeedsCleanUp(running with { Details = null }));
    }

    [Fact]
    public void After_a_shell_ended_failed_or_was_stopped_or_before_one_started_a_stop_needs_no_clean_up()
    {
        Assert.False(DetailReducer.ShellNeedsCleanUp(Loaded()));
        Assert.False(DetailReducer.ShellNeedsCleanUp(Reduce(Running(), new ExecEnded(1, 0, false)).State));
        Assert.False(DetailReducer.ShellNeedsCleanUp(Reduce(Running(), new ExecFailed(1, "broke")).State));
        Assert.False(DetailReducer.ShellNeedsCleanUp(Reduce(Running(), new ExecStopRequested()).State));
    }

    [Fact]
    public void Stopping_a_shell_in_a_container_that_no_longer_runs_only_stops_it()
    {
        var running = Running();
        var refreshed = Reduce(running, new DetailRefreshRequested()).State;
        var paused = Reduce(refreshed, new InspectLoaded(refreshed.InspectGeneration, Inspection(paused: true))).State;

        var (_, effects) = Reduce(paused, new ExecStopRequested());

        Assert.Equal([new StopExecEffect()], effects);
    }

    [Fact]
    public void Stop_after_it_ended_only_clears_the_end()
    {
        var ended = Reduce(Running(), new ExecEnded(1, 0, false)).State;

        var (state, effects) = Reduce(ended, new ExecStopRequested());
        var (again, none) = Reduce(state, new ExecStopRequested());

        Assert.Equal(ExecStatus.NotStarted, state.Exec.Status);
        Assert.Equal(1, state.Exec.Generation);
        Assert.Null(state.Exec.ExitCode);
        Assert.Empty(effects);
        Assert.Same(state, again);
        Assert.Empty(none);
    }

    [Fact]
    public void Start_again_after_ended_is_a_new_generation()
    {
        var ended = Reduce(Running(), new ExecEnded(1, 127, false)).State;
        ImmutableArray<string> bash = ["bash"];

        var (state, effects) = Reduce(ended, new ExecStartRequested(bash, Wide));

        Assert.Equal(ExecStatus.Starting, state.Exec.Status);
        Assert.Equal(2, state.Exec.Generation);
        Assert.Null(state.Exec.ExitCode);
        Assert.Null(state.Exec.ExecId);
        Assert.Equal([new StartExecEffect(2, bash, Wide)], effects);

        var failed = Reduce(Starting(), new ExecFailed(1, "broke")).State;
        var (retried, _) = Reduce(failed, new ExecStartRequested(Sh, Size));
        Assert.Equal(ExecStatus.Starting, retried.Exec.Status);
        Assert.Null(retried.Exec.Error);
    }

    [Fact]
    public void Results_of_an_older_generation_are_ignored()
    {
        var ended = Reduce(Running(), new ExecEnded(1, 0, false)).State;
        var second = Reduce(ended, new ExecStartRequested(Sh, Size)).State;

        foreach (var old in new DetailEvent[] { new ExecStarted(1, "old"), new ExecEnded(1, 1, true), new ExecFailed(1, "old") })
        {
            var (state, effects) = Reduce(second, old);
            Assert.Same(second, state);
            Assert.Empty(effects);
        }
    }

    [Fact]
    public void Losing_the_connection_resets_the_shell()
    {
        var (state, effects) = Reduce(Running(), new DetailConnectionLost());

        Assert.Equal(ExecStatus.NotStarted, state.Exec.Status);
        Assert.Equal(2, state.Exec.Generation);
        Assert.Empty(effects);
        Assert.Same(state, Reduce(state, new ExecEnded(1, 0, false)).State);
    }

    [Fact]
    public void A_removed_container_stops_the_shell()
    {
        var running = Running();
        var refreshed = Reduce(running, new DetailRefreshRequested()).State;

        var (state, effects) = Reduce(refreshed, new InspectFailed(refreshed.InspectGeneration, "No such container: c", NotFound: true));

        Assert.True(state.Removed);
        Assert.Equal(ExecStatus.NotStarted, state.Exec.Status);
        Assert.Equal(2, state.Exec.Generation);
        Assert.Equal([new StopDetailStreamsEffect(), new StopExecEffect()], effects);
    }

    [Fact]
    public void Log_and_stats_events_leave_the_shell_alone()
    {
        var running = Running();
        var logs = Reduce(running, new LogLinesArrived(running.StreamGeneration, [new LogLine(LogStream.Stdout, null, "x")])).State;
        var stats = Reduce(logs, new StatsSampleArrived(running.StreamGeneration, new StatsSample(
            DateTimeOffset.UnixEpoch, 1, 1, 1, 0, 0, 0, 0, 1))).State;
        var ended = Reduce(stats, new LogStreamEnded(running.StreamGeneration, "broke")).State;

        Assert.Same(running.Exec, ended.Exec);

        var (resized, _) = Reduce(ended, new ExecResizeRequested(Wide));
        var (stopped, _) = Reduce(resized, new ExecStopRequested());
        Assert.Same(ended.Logs, stopped.Logs);
        Assert.Same(ended.Stats, stopped.Stats);
        Assert.Equal(ended.LogStatus, stopped.LogStatus);
        Assert.Equal(ended.StatsStatus, stopped.StatsStatus);
        Assert.Equal(ended.StreamGeneration, stopped.StreamGeneration);
        Assert.Equal(ended.InspectGeneration, stopped.InspectGeneration);
    }
}

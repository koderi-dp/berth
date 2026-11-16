using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Berth.Engine;
using Berth.Engine.Models;
using Berth.Session;
using Microsoft.Extensions.Time.Testing;

namespace Berth.Tests.Session;

public sealed class ContainerDetailSessionExecTests
{
    /// <summary>Only a safety net so a broken runtime fails the test instead of hanging it; tests never wait for time.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly ImmutableArray<string> Sh = ["sh"];

    private static readonly TerminalSize Size = new(80, 24);

    [Fact]
    public async Task Output_reaches_the_sink_in_order_and_unchanged()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync();

        // 0xFF 0xFE are not UTF-8; U+1F600 (F0 9F 98 80) is cut in the middle by the two reads.
        await shell.WriteOutputAsync([0x41, 0xFF, 0xFE, 0xF0, 0x9F]);
        await h.Sink.WhenReceived(5);
        await shell.WriteOutputAsync([0x98, 0x80, 0x0D, 0x0A]);
        await h.Sink.WhenReceived(9);

        Assert.Equal([0x41, 0xFF, 0xFE, 0xF0, 0x9F, 0x98, 0x80, 0x0D, 0x0A], h.Sink.All);
        Assert.Equal(2, h.Sink.Writes.Count);
        Assert.All(h.Sink.Writes, w => Assert.Equal(1, w.Generation));
    }

    [Fact]
    public async Task The_pump_waits_while_the_sink_is_busy()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync();
        var busy = h.Sink.Hold();

        await shell.WriteOutputAsync([0x61]);
        await h.Sink.WhenReceived(1);
        await shell.WriteOutputAsync([0x62]);
        // A round trip through the session's input shows it is not stuck, yet no second read has started.
        h.Session.SendExecInput(1, new byte[] { 0x0D });
        await shell.ReadInputAsync(1).WaitAsync(Patience);

        Assert.Equal(1, shell.Reads);

        busy.Release();
        await h.Sink.WhenReceived(2);
        Assert.Equal([0x61, 0x62], h.Sink.All);
    }

    [Fact]
    public async Task Input_is_written_while_output_is_blocked()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync();
        h.Sink.Hold();
        await shell.WriteOutputAsync(new byte[32 * 1024]);
        await h.Sink.WhenReceived(1);

        h.Session.SendExecInput(1, new byte[] { 0x03 });

        Assert.Equal([0x03], await shell.ReadInputAsync(1).WaitAsync(Patience));
    }

    [Fact]
    public async Task Input_for_an_older_generation_is_dropped()
    {
        await using var h = new Harness();
        var first = await h.RunningAsync();
        h.Session.StopExec();
        await first.WriteClosed.WaitAsync(Patience);
        first.End();
        await first.Disposed.WaitAsync(Patience);
        await h.WaitFor(s => s.Exec is { Status: ExecStatus.NotStarted, Generation: 2 });

        h.Session.StartExec(Sh, Size);
        var second = await h.Api.NextShellAsync().WaitAsync(Patience);
        await h.WaitFor(s => s.Exec is { Status: ExecStatus.Running, Generation: 3 });
        h.Session.SendExecInput(1, new byte[] { 0x61 });
        h.Session.SendExecInput(2, new byte[] { 0x62 });
        h.Session.SendExecInput(3, new byte[] { 0x63 });

        Assert.Equal([0x63], await second.ReadInputAsync(1).WaitAsync(Patience));
        Assert.Empty(await first.ReadAllInputAsync().WaitAsync(Patience));
    }

    [Fact]
    public async Task A_megabyte_of_input_arrives_whole_and_in_order()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync();
        var paste = new byte[1024 * 1024];
        new Random(4).NextBytes(paste);

        // One buffer reused for every piece, as a caller would: the session copies what it is given.
        var piece = new byte[16 * 1024];
        for (var at = 0; at < paste.Length; at += piece.Length)
        {
            paste.AsSpan(at, piece.Length).CopyTo(piece);
            h.Session.SendExecInput(1, piece);
            Array.Clear(piece);
        }

        Assert.Equal(paste, await shell.ReadInputAsync(paste.Length).WaitAsync(Patience));
    }

    [Fact]
    public async Task The_engine_closing_the_stream_ends_with_the_exit_code()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync();
        h.Api.ScriptExecInspections(new ExecInspection(true, null), new ExecInspection(true, null), new ExecInspection(false, 3));
        h.Session.SendExecInput(1, new byte[] { 0x65 });
        await shell.ReadInputAsync(1).WaitAsync(Patience);

        shell.End();
        await h.WhenWaiting(1);
        h.Time.Advance(ContainerDetailSession.ExecPollInterval);
        await h.WhenWaiting(2);
        h.Time.Advance(ContainerDetailSession.ExecPollInterval);
        var state = await h.WaitFor(s => s.Exec.Status == ExecStatus.Ended);

        Assert.Equal(3, state.Exec.ExitCode);
        Assert.True(state.Exec.HadInput);
        Assert.Equal(3, h.Api.Calls.Count(c => c == "InspectExec exec-1"));
        await shell.Disposed.WaitAsync(Patience);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task An_exit_code_the_engine_never_gives_is_null_after_five_tries()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync();
        h.Api.ScriptExecInspections(new ExecInspection(true, null));

        shell.End();
        for (var wait = 1; wait <= 4; wait++)
        {
            await h.WhenWaiting(wait);
            h.Time.Advance(ContainerDetailSession.ExecPollInterval);
        }

        var state = await h.WaitFor(s => s.Exec.Status == ExecStatus.Ended);

        Assert.Null(state.Exec.ExitCode);
        Assert.False(state.Exec.HadInput);
        Assert.Equal(5, h.Api.Calls.Count(c => c == "InspectExec exec-1"));
    }

    [Fact]
    public async Task A_failed_create_is_shown_and_reported_in_fixed_wording()
    {
        const string Message = "Container c is paused, unpause the container before exec";
        var api = new FakeEngineApi();
        api.SetInspection(FakeEngineApi.Inspection("c"));
        var broken = new EngineException(409, Message);
        api.Fail("CreateExec", broken);
        await using var h = new Harness(api);
        await h.OpenAsync();

        h.Session.StartExec(["sh", "-c", "echo s3cret"], Size);
        var state = await h.WaitFor(s => s.Exec.Status == ExecStatus.Failed);
        var error = await h.WaitForError();

        Assert.Equal(Message, state.Exec.Error);
        Assert.Equal("the exec stream failed", error.What);
        Assert.DoesNotContain("s3cret", error.What, StringComparison.Ordinal);
        Assert.DoesNotContain(Message, error.What, StringComparison.Ordinal);
        Assert.Same(broken, error.Exception);
    }

    [Fact]
    public async Task A_dropped_connection_mid_shell_is_failed_with_its_message()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync();

        shell.Fail(new IOException("An existing connection was forcibly closed by the remote host."));
        var state = await h.WaitFor(s => s.Exec.Status == ExecStatus.Failed);
        var error = await h.WaitForError();

        Assert.Equal("An existing connection was forcibly closed by the remote host.", state.Exec.Error);
        Assert.Equal("the exec stream failed", error.What);
        await shell.Disposed.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_terminal_that_throws_fails_the_shell_in_berths_words_not_its_message()
    {
        const string Leaked = "bytes the terminal could not take: s3cret";
        await using var h = new Harness();
        var shell = await h.RunningAsync(attachSink: false);
        var thrown = new InvalidOperationException(Leaked);
        h.Session.AttachExecOutput(new ThrowingSink(thrown));

        await shell.WriteOutputAsync("hello"u8.ToArray());
        var state = await h.WaitFor(s => s.Exec.Status == ExecStatus.Failed);
        var error = await h.WaitForError();

        Assert.Equal("The terminal stopped working. Start again.", state.Exec.Error);
        Assert.Equal(ContainerDetailSession.TerminalFailedText, state.Exec.Error);
        Assert.Equal("the terminal failed", error.What);
        Assert.Same(thrown, error.Exception);
        await shell.Disposed.WaitAsync(Patience);
    }

    [Fact]
    public async Task Stop_closes_the_stream_and_raises_nothing_more()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync();

        h.Session.StopExec();
        await h.WaitFor(s => s.Exec is { Status: ExecStatus.NotStarted, Generation: 2 });
        var raised = 0;
        h.Session.StateChanged += _ => Interlocked.Increment(ref raised);

        // The write side is closed first, so the shell reads the end of its input; the stream stays open for its last words.
        await shell.WriteClosed.WaitAsync(Patience);
        Assert.False(shell.Disposed.IsCompleted);
        await shell.WriteOutputAsync([0x62, 0x79, 0x65]);
        shell.End();
        await shell.Disposed.WaitAsync(Patience);
        h.Session.SendExecInput(1, new byte[] { 0x61 });
        await h.Session.DisposeAsync();

        Assert.Empty(h.Sink.Writes);
        Assert.Equal(0, Volatile.Read(ref raised));
        Assert.Equal(1, h.Api.Calls.Count(c => c.StartsWith("CreateExec", StringComparison.Ordinal)));
        Assert.DoesNotContain(h.Api.Calls, c => c.StartsWith("InspectExec", StringComparison.Ordinal));
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task Stop_disposes_a_stream_the_engine_keeps_open_after_the_grace()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync();

        h.Session.StopExec();
        await shell.WriteClosed.WaitAsync(Patience);
        await h.WhenWaiting(1);
        h.Time.Advance(ContainerDetailSession.ExecCloseGrace - TimeSpan.FromMilliseconds(1));
        Assert.False(shell.Disposed.IsCompleted);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));

        await shell.Disposed.WaitAsync(Patience);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task Stop_on_a_stream_without_a_write_side_to_close_disposes_it_at_once()
    {
        var api = new FakeEngineApi { ShellsCanCloseWrite = false };
        api.SetInspection(FakeEngineApi.Inspection("c"));
        await using var h = new Harness(api);
        var shell = await h.RunningAsync();

        h.Session.StopExec();

        await shell.WriteClosed.WaitAsync(Patience);
        await shell.Disposed.WaitAsync(Patience);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task Stop_closes_the_write_side_only_after_a_pending_write_has_ended()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync();
        var writes = shell.HoldWrites();
        h.Session.SendExecInput(1, new byte[] { 0x61 });
        await writes.Entered.WaitAsync(Patience);

        h.Session.StopExec();
        await shell.WriteClosed.WaitAsync(Patience);

        Assert.False(shell.ClosedWriteDuringAWrite);
        shell.End();
        await shell.Disposed.WaitAsync(Patience);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task Stop_while_starting_cancels_the_start()
    {
        var api = new FakeEngineApi();
        api.SetInspection(FakeEngineApi.Inspection("c"));
        var start = api.Hold("StartExec");
        await using var h = new Harness(api);
        await h.OpenAsync();
        h.Session.StartExec(Sh, Size);
        await start.Entered.WaitAsync(Patience);

        h.Session.StopExec();

        await api.WhenCancelled("StartExec exec-1").WaitAsync(Patience);
        var state = await h.WaitFor(s => s.Exec is { Status: ExecStatus.NotStarted, Generation: 2 });
        await h.Session.DisposeAsync();
        Assert.Equal(ExecStatus.NotStarted, state.Exec.Status);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task Dispose_closes_the_stream()
    {
        var h = new Harness();
        var shell = await h.RunningAsync();

        await h.DisposeAsync();

        Assert.True(shell.WriteClosed.IsCompleted);
        Assert.True(shell.Disposed.IsCompleted);
        Assert.False(shell.ClosedWriteDuringAWrite);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task A_cut_off_closes_the_stream_and_reports_no_error()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync();

        await h.Session.CutOffAsync();

        Assert.True(shell.Disposed.IsCompleted);
        var state = await h.WaitFor(s => s.ConnectionLost);
        Assert.Equal(ExecStatus.NotStarted, state.Exec.Status);
        Assert.Equal(2, state.Exec.Generation);
        Assert.Empty(h.Sink.Writes);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task Each_shell_carries_a_marker_of_its_own()
    {
        await using var h = new Harness();
        await h.RunningAsync();
        h.Session.StopExec();
        await h.WaitFor(s => s.Exec is { Status: ExecStatus.NotStarted, Generation: 2 });
        h.Session.StartExec(Sh, Size);
        await h.WaitFor(s => s.Exec is { Status: ExecStatus.Running, Generation: 3 });

        var environments = h.Api.ExecCreates.Select(c => c.Environment).ToArray();

        Assert.Equal(2, environments.Length);
        Assert.All(environments, env => Assert.Matches("^BERTH_EXEC=[0-9a-f]{16}$", Assert.Single(env)));
        Assert.NotEqual(environments[0][0], environments[1][0]);
    }

    [Fact]
    public async Task Stopping_a_running_shell_cleans_up_by_its_marker_in_the_same_container()
    {
        await using var h = new Harness();
        await h.RunningAsync();

        h.Session.StopExec();
        var run = await h.NextCleanUpAsync();

        Assert.Equal("c", run.ContainerId);
        Assert.Equal(ExecCleanUp.Command(h.MarkerOf(0)), run.Command);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task A_shell_whose_terminal_failed_is_cleaned_up_by_its_marker()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync(attachSink: false);
        h.Session.AttachExecOutput(new ThrowingSink(new InvalidOperationException("page gone")));

        await shell.WriteOutputAsync("hello"u8.ToArray());
        var run = await h.NextCleanUpAsync();

        Assert.Equal("c", run.ContainerId);
        Assert.Equal(ExecCleanUp.Command(h.MarkerOf(0)), run.Command);
        Assert.Equal(ExecStatus.Failed, (await h.WaitFor(s => s.Exec.Status == ExecStatus.Failed)).Exec.Status);
    }

    [Fact]
    public async Task A_shell_whose_stream_broke_is_cleaned_up_once_even_when_the_tab_then_stops_it()
    {
        var h = new Harness();
        var shell = await h.RunningAsync();

        shell.Fail(new IOException("An existing connection was forcibly closed by the remote host."));
        await h.NextCleanUpAsync();
        h.Session.StopExec();
        await h.WaitFor(s => s.Exec.Status == ExecStatus.NotStarted);
        await h.DisposeAsync();

        Assert.Single(h.Api.Calls, c => c.StartsWith("RunDetachedExec", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_shell_that_ended_by_itself_is_not_cleaned_up()
    {
        var h = new Harness();
        var shell = await h.RunningAsync();
        shell.End();
        await h.WaitFor(s => s.Exec.Status == ExecStatus.Ended);

        h.Session.StopExec();
        await h.WaitFor(s => s.Exec.Status == ExecStatus.NotStarted);
        await h.DisposeAsync();

        Assert.Empty(h.Api.DetachedExecs);
        Assert.DoesNotContain(h.Api.Calls, c => c.StartsWith("RunDetachedExec", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disposing_with_a_shell_running_cleans_it_up_before_returning()
    {
        var h = new Harness();
        await h.RunningAsync();

        await h.DisposeAsync();

        var run = Assert.Single(h.Api.DetachedExecs);
        Assert.Equal(ExecCleanUp.Command(h.MarkerOf(0)), run.Command);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task A_stop_then_a_dispose_cleans_up_once_and_the_dispose_waits_for_it()
    {
        var h = new Harness();
        await h.RunningAsync();
        var gate = h.Api.Hold("RunDetachedExec");

        h.Session.StopExec();
        await gate.Entered.WaitAsync(Patience);
        var dispose = h.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);

        gate.Release();
        await dispose.WaitAsync(Patience);
        Assert.Single(h.Api.DetachedExecs);
        Assert.Single(h.Api.Calls, c => c.StartsWith("RunDetachedExec", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cut_off_cleans_up_before_it_returns_so_the_client_is_still_there()
    {
        await using var h = new Harness();
        await h.RunningAsync();

        await h.Session.CutOffAsync();

        var run = Assert.Single(h.Api.DetachedExecs);
        Assert.Equal(ExecCleanUp.Command(h.MarkerOf(0)), run.Command);
    }

    [Fact]
    public async Task A_clean_up_that_fails_is_reported_in_fixed_words_and_shown_nowhere()
    {
        await using var h = new Harness();
        await h.RunningAsync();
        h.Api.Fail("RunDetachedExec", new EngineException(409, "Container c is paused"));

        h.Session.StopExec();
        var (what, ex) = await h.WaitForError();

        Assert.Equal("ending a shell left in the container failed", what);
        Assert.IsType<EngineException>(ex);
        var state = h.Session.State;
        Assert.Equal(ExecStatus.NotStarted, state.Exec.Status);
        Assert.Null(state.Exec.Error);
        Assert.Null(state.LoadError);
    }

    [Fact]
    public async Task A_clean_up_the_engine_does_not_answer_is_given_up_after_its_limit()
    {
        var h = new Harness();
        await h.RunningAsync();
        var gate = h.Api.Hold("RunDetachedExec");

        var dispose = h.DisposeAsync().AsTask();
        await gate.Entered.WaitAsync(Patience);
        h.Time.Advance(ContainerDetailSession.ExecCleanUpLimit - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));

        await dispose.WaitAsync(Patience);
        Assert.Contains("RunDetachedExec c", h.Api.Cancelled);
        Assert.Empty(h.Api.DetachedExecs);
    }

    [Fact]
    public async Task Bytes_with_no_sink_attached_are_dropped_and_the_pump_goes_on()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync(attachSink: false);

        await shell.WriteOutputAsync([0x61]);
        await shell.WhenReading(2).WaitAsync(Patience);
        h.Session.AttachExecOutput(h.Sink);
        await shell.WriteOutputAsync([0x62]);
        await h.Sink.WhenReceived(1);

        Assert.Equal([0x62], h.Sink.All);
    }

    [Fact]
    public async Task A_resize_reaches_the_engine_with_the_exec_id()
    {
        var api = new FakeEngineApi { ExecId = "exec-7" };
        api.SetInspection(FakeEngineApi.Inspection("c"));
        var resize = api.Hold("ResizeExec");
        await using var h = new Harness(api);
        await h.RunningAsync();

        h.Session.ResizeExec(new TerminalSize(120, 30));
        await resize.Entered.WaitAsync(Patience);
        resize.Release();
        // Disposing straight after the release could cancel the held call before it sees the release.
        Assert.True(SpinWait.SpinUntil(() => !api.Resizes.IsEmpty, Patience));
        await h.Session.DisposeAsync();

        Assert.Equal([("exec-7", new TerminalSize(120, 30))], api.Resizes);
    }

    [Fact]
    public async Task A_failed_resize_is_reported_and_not_shown()
    {
        var api = new FakeEngineApi();
        api.SetInspection(FakeEngineApi.Inspection("c"));
        api.Fail("ResizeExec", new EngineException(500, "resize broke"));
        await using var h = new Harness(api);
        await h.RunningAsync();

        h.Session.ResizeExec(new TerminalSize(120, 30));
        var error = await h.WaitForError();

        Assert.Equal("resizing the terminal failed", error.What);
        Assert.Equal(ExecStatus.Running, h.Session.State.Exec.Status);
        Assert.Null(h.Session.State.Exec.Error);
    }

    [Fact]
    public async Task Input_past_two_mebibytes_queued_for_a_shell_that_does_not_read_is_dropped()
    {
        const int Piece = 64 * 1024;
        await using var h = new Harness();
        var shell = await h.RunningAsync();
        var held = shell.HoldWrites();

        // Three mebibytes for a shell that reads nothing (the first piece is held in the write): only the first two are kept.
        var pieces = 3 * ExecInput.QueueLimit / 2 / Piece;
        var outcomes = new List<ExecInputOutcome>();
        for (var i = 0; i < pieces; i++)
        {
            outcomes.Add(h.Session.SendExecInput(1, Enumerable.Repeat((byte)i, Piece).ToArray()));
            if (i == 0)
                await held.Entered.WaitAsync(Patience);
        }

        Assert.Equal(ExecInput.QueueLimit, h.Session.QueuedExecInput);
        // Each piece says whether it was taken, so the tab can tell the user that input was dropped.
        var taken = ExecInput.QueueLimit / Piece;
        Assert.Equal([.. Enumerable.Repeat(ExecInputOutcome.Sent, taken), .. Enumerable.Repeat(ExecInputOutcome.Dropped, pieces - taken)], outcomes);

        held.Release();
        var kept = ExecInput.QueueLimit / Piece;
        var received = await shell.ReadInputAsync(kept * Piece).WaitAsync(Patience);
        Assert.Equal([.. Enumerable.Range(0, kept).SelectMany(i => Enumerable.Repeat((byte)i, Piece))], received);

        // Once the queue has drained input is taken again, and nothing of what was dropped comes before it.
        Assert.True(SpinWait.SpinUntil(() => h.Session.QueuedExecInput == 0, Patience));
        Assert.Equal(ExecInputOutcome.Sent, h.Session.SendExecInput(1, "Z"u8.ToArray()));
        Assert.Equal("Z"u8.ToArray(), await shell.ReadInputAsync(1).WaitAsync(Patience));
    }

    [Fact]
    public async Task Input_for_a_shell_that_is_not_the_live_one_is_not_counted_as_dropped()
    {
        await using var h = new Harness();
        await h.RunningAsync();

        Assert.Equal(ExecInputOutcome.Ignored, h.Session.SendExecInput(2, "a"u8.ToArray()));
        Assert.Equal(ExecInputOutcome.Sent, h.Session.SendExecInput(1, "a"u8.ToArray()));
    }

    [Fact]
    public async Task Resizes_are_sent_one_at_a_time_and_only_the_newest_waiting_one()
    {
        await using var h = new Harness();
        await h.RunningAsync();
        var held = h.Api.Hold("ResizeExec");

        h.Session.ResizeExec(new TerminalSize(100, 30));
        await held.Entered.WaitAsync(Patience);
        var newest = NextChange(h.Session, s => s.Exec.Size == new TerminalSize(120, 32));
        h.Session.ResizeExec(new TerminalSize(110, 31));
        h.Session.ResizeExec(new TerminalSize(120, 32));
        await newest;
        held.Release();

        Assert.True(SpinWait.SpinUntil(() => h.Api.Resizes.Any(r => r.Size == new TerminalSize(120, 32)), Patience));
        Assert.Equal([("exec-1", new TerminalSize(100, 30)), ("exec-1", new TerminalSize(120, 32))], h.Api.Resizes);
        Assert.Equal(2, h.Api.Calls.Count(c => c == "ResizeExec exec-1"));
    }

    [Fact]
    public async Task A_stop_while_the_exit_code_is_read_ends_the_reading_and_reports_nothing()
    {
        await using var h = new Harness();
        var shell = await h.RunningAsync();
        var inspect = h.Api.Hold("InspectExec");
        h.Api.Fail("InspectExec", new EngineException(500, "inspect broke"));

        shell.End();
        await inspect.Entered.WaitAsync(Patience);
        var stopped = NextChange(h.Session, s => s.Exec.Status == ExecStatus.NotStarted);
        h.Session.StopExec();
        await stopped;
        inspect.Release();
        await shell.Disposed.WaitAsync(Patience);

        Assert.Empty(h.Errors);
        Assert.Equal(1, h.Api.Calls.Count(c => c == "InspectExec exec-1"));
        Assert.Equal(ExecStatus.NotStarted, h.Session.State.Exec.Status);
    }

    [Fact]
    public async Task A_close_write_that_outlasts_the_grace_has_its_failure_observed()
    {
        var unobserved = new ConcurrentQueue<AggregateException>();
        void Unobserved(object? sender, UnobservedTaskExceptionEventArgs e) => unobserved.Enqueue(e.Exception);
        TaskScheduler.UnobservedTaskException += Unobserved;
        try
        {
            var failure = await AbandonACloseWriteAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.DoesNotContain(unobserved, e => e.Flatten().InnerExceptions.Contains(failure));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Unobserved;
        }
    }

    /// <summary>Stops a shell whose close-write hangs past the grace, then fails that close-write; nothing of it is kept.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Exception> AbandonACloseWriteAsync()
    {
        var failure = new IOException("close_notify failed after the stream was disposed");
        await using var h = new Harness();
        var shell = await h.RunningAsync();
        var closing = shell.HangCloseWrite();

        h.Session.StopExec();
        await shell.WriteClosed.WaitAsync(Patience);
        // The grace is a timer on the fake clock that starts just after the close-write; move the clock until it has run out.
        while (!shell.Disposed.IsCompleted)
        {
            h.Time.Advance(ContainerDetailSession.ExecCloseGrace);
            await Task.WhenAny(shell.Disposed, Task.Delay(10));
        }

        closing.SetException(failure);
        return failure;
    }

    /// <summary>Completes with the first state change after this call that matches: its effects have been started by then.</summary>
    private static Task<DetailState> NextChange(ContainerDetailSession session, Func<DetailState, bool> match)
    {
        var found = new TaskCompletionSource<DetailState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(DetailState s)
        {
            if (match(s) && found.TrySetResult(s))
                session.StateChanged -= Check;
        }

        session.StateChanged += Check;
        return found.Task.WaitAsync(Patience);
    }

    /// <summary>The terminal's end: records what it is given, and can hold its writes to push back on the pump.</summary>
    private sealed class Sink : IExecOutput
    {
        private readonly Lock _lock = new();
        private readonly List<(int Count, TaskCompletionSource Done)> _waiters = [];
        private volatile FakeEngineApi.Gate? _hold;
        private int _bytes;

        public ConcurrentQueue<(int Generation, byte[] Bytes)> Writes { get; } = new();

        public byte[] All => [.. Writes.SelectMany(w => w.Bytes)];

        /// <summary>Makes later writes wait until the returned gate is released (or the pump's token is cancelled).</summary>
        public FakeEngineApi.Gate Hold() => _hold = new FakeEngineApi.Gate();

        /// <summary>Completes once <paramref name="count"/> bytes in all have been given to the sink.</summary>
        public Task WhenReceived(int count)
        {
            lock (_lock)
            {
                if (_bytes >= count)
                    return Task.CompletedTask;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, done));
                return done.Task.WaitAsync(Patience);
            }
        }

        public async ValueTask WriteAsync(int generation, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            Writes.Enqueue((generation, bytes.ToArray()));
            List<TaskCompletionSource> reached;
            lock (_lock)
            {
                _bytes += bytes.Length;
                reached = _waiters.Where(w => w.Count <= _bytes).Select(w => w.Done).ToList();
                _waiters.RemoveAll(w => w.Count <= _bytes);
            }

            reached.ForEach(done => done.TrySetResult());
            if (_hold is { } hold)
                await hold.Released.WaitAsync(ct);
        }
    }

    /// <summary>A terminal that fails on every write.</summary>
    private sealed class ThrowingSink(Exception error) : IExecOutput
    {
        public ValueTask WriteAsync(int generation, ReadOnlyMemory<byte> bytes, CancellationToken ct) => ValueTask.FromException(error);
    }

    /// <summary>A detail session for container c over a fake engine, with a fake clock and a terminal sink.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<(string What, Exception Exception)> _errors = new();
        private readonly SemaphoreSlim _errorArrived = new(0);
        private readonly Lock _lock = new();
        private readonly List<(int Count, TaskCompletionSource Done)> _waiters = [];
        private int _waits;

        public Harness(FakeEngineApi? api = null)
        {
            Api = api ?? new FakeEngineApi();
            if (api is null)
                Api.SetInspection(FakeEngineApi.Inspection("c"));
            Session = new ContainerDetailSession(
                Api,
                "c",
                Time,
                (what, ex) =>
                {
                    _errors.Enqueue((what, ex));
                    _errorArrived.Release();
                });
            Session.ExecWaiting += Waiting;
        }

        public FakeEngineApi Api { get; }

        public FakeTimeProvider Time { get; } = new();

        public ContainerDetailSession Session { get; }

        public Sink Sink { get; } = new();

        public IReadOnlyCollection<(string What, Exception Exception)> Errors => _errors;

        /// <summary>Opens the page and waits for the first read of the running container.</summary>
        public async Task OpenAsync()
        {
            Session.Open();
            await WaitFor(s => s.Details is not null);
        }

        /// <summary>Opens the page, starts sh (generation 1) and returns its shell once the state says it runs.</summary>
        public async Task<FakeEngineApi.FakeShell> RunningAsync(bool attachSink = true)
        {
            await OpenAsync();
            if (attachSink)
                Session.AttachExecOutput(Sink);
            Session.StartExec(Sh, Size);
            var shell = await Api.NextShellAsync().WaitAsync(Patience);
            await WaitFor(s => s.Exec.Status == ExecStatus.Running);
            return shell;
        }

        public Task<DetailState> WaitFor(Func<DetailState, bool> match) => ContainerDetailSessionTests.WaitFor(Session, match);

        /// <summary>The marker the <paramref name="index"/>th exec create carried.</summary>
        public string MarkerOf(int index) => Assert.Single(Api.ExecCreates.ElementAt(index).Environment)["BERTH_EXEC=".Length..];

        /// <summary>Completes with the first detached exec run (the clean-up), polling: the run is recorded on a thread-pool thread.</summary>
        public async Task<(string ContainerId, IReadOnlyList<string> Command)> NextCleanUpAsync()
        {
            using var patience = new CancellationTokenSource(Patience);
            while (!Api.DetachedExecs.TryPeek(out _))
                await Task.Delay(10, patience.Token);
            Api.DetachedExecs.TryPeek(out var run);
            return run;
        }

        /// <summary>Completes once the session has started waiting on the clock <paramref name="count"/> times in all.</summary>
        public Task WhenWaiting(int count)
        {
            lock (_lock)
            {
                if (_waits >= count)
                    return Task.CompletedTask;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, done));
                return done.Task.WaitAsync(Patience);
            }
        }

        /// <summary>Completes with the first reported error, earlier or later.</summary>
        public async Task<(string What, Exception Exception)> WaitForError()
        {
            if (!await _errorArrived.WaitAsync(Patience))
                throw new TimeoutException("No error was reported.");
            _errors.TryPeek(out var error);
            return error;
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();

        private void Waiting()
        {
            List<TaskCompletionSource> reached;
            lock (_lock)
            {
                _waits++;
                reached = _waiters.Where(w => w.Count <= _waits).Select(w => w.Done).ToList();
                _waiters.RemoveAll(w => w.Count <= _waits);
            }

            reached.ForEach(done => done.TrySetResult());
        }
    }
}

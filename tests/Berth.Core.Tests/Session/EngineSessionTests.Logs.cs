using Berth.Engine;
using Berth.Engine.Models;
using Berth.Session;

namespace Berth.Tests.Session;

public sealed partial class EngineSessionTests
{
    private static readonly DateTimeOffset LogT0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Completes with the first logs state, current or later, that matches <paramref name="match"/>.</summary>
    private static async Task<LogsState> WaitForLogs(LogsSession logs, Func<LogsState, bool> match)
    {
        var found = new TaskCompletionSource<LogsState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(LogsState s)
        {
            if (match(s))
                found.TrySetResult(s);
        }

        logs.StateChanged += Check;
        try
        {
            Check(logs.State);
            return await found.Task.WaitAsync(Patience);
        }
        finally
        {
            logs.StateChanged -= Check;
        }
    }

    /// <summary>Completes once the session has collected <paramref name="count"/> lines for its next tick.</summary>
    private static Task WaitForCollected(LogsSession logs, int count)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        logs.LinesCollected += n =>
        {
            if (n >= count)
                done.TrySetResult();
        };
        return done.Task.WaitAsync(Patience);
    }

    private static FakeEngineApi LogsApi(params string[] running)
    {
        var api = new FakeEngineApi { Containers = [.. running.Select(FakeEngineApi.Container)] };
        foreach (var id in running)
        {
            api.SetInspection(FakeEngineApi.Inspection(id, tty: id.StartsWith('t')));
            api.LogsOf(id); // its own stream, made before the session opens it
        }
        return api;
    }

    [Fact]
    public async Task No_logs_session_opens_before_the_engine_is_connected()
    {
        await using var h = Harness.With(LogsApi("a"));

        Assert.Null(h.Session.OpenLogs());
    }

    [Fact]
    public async Task The_logs_session_follows_each_running_container_with_its_own_framing()
    {
        var api = LogsApi("a", "tty");
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        await using var logs = h.Session.OpenLogs();
        Assert.NotNull(logs);
        await api.LogsOf("a").WhenOpened().WaitAsync(Patience);
        await api.LogsOf("tty").WhenOpened().WaitAsync(Patience);

        Assert.Contains("Logs a tail=100 follow=True tty=False", api.Calls);
        Assert.Contains("Logs tty tail=100 follow=True tty=True", api.Calls);
    }

    [Fact]
    public async Task Lines_of_every_container_arrive_together_once_a_tick()
    {
        var api = LogsApi("a", "b");
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        await using var logs = h.Session.OpenLogs();
        Assert.NotNull(logs);
        await api.LogsOf("a").WhenOpened().WaitAsync(Patience);
        await api.LogsOf("b").WhenOpened().WaitAsync(Patience);

        var collected = WaitForCollected(logs, 3);
        api.LogsOf("a").Push(new LogLine(LogStream.Stdout, LogT0.AddSeconds(2), "a later"));
        api.LogsOf("b").Push(new LogLine(LogStream.Stderr, LogT0, "b first"));
        api.LogsOf("a").Push(new LogLine(LogStream.Stdout, LogT0.AddSeconds(3), "a last"));
        await collected;

        var changes = 0;
        logs.StateChanged += _ => Interlocked.Increment(ref changes);
        h.Time.Advance(LogsSession.LogTick);
        var state = await WaitForLogs(logs, s => s.Log.Entries.Count == 3);

        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Equal(["b first", "a later", "a last"], state.Log.Entries.Select(e => e.Line.Text));
    }

    [Fact]
    public async Task A_container_that_starts_later_joins_and_one_that_stops_keeps_its_lines()
    {
        var api = LogsApi("a");
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        await using var logs = h.Session.OpenLogs();
        Assert.NotNull(logs);
        await api.LogsOf("a").WhenOpened().WaitAsync(Patience);

        api.LogsOf("a").Push(new LogLine(LogStream.Stdout, LogT0, "bye"));
        api.LogsOf("a").End();
        var ended = await WaitForLogs(logs, s => s.Sources["a"].Status == LogSourceStatus.Ended);
        Assert.Equal("bye", Assert.Single(ended.Log.Entries).Line.Text);

        api.SetInspection(FakeEngineApi.Inspection("b"));
        api.LogsOf("b");
        api.Containers = [FakeEngineApi.Container("a") with { State = "exited" }, FakeEngineApi.Container("b")];
        h.Session.Dispatch(new ReloadRequested());
        await api.LogsOf("b").WhenOpened().WaitAsync(Patience);

        var state = await WaitForLogs(logs, s => s.Sources.ContainsKey("b"));
        Assert.Equal(LogSourceStatus.Live, state.Sources["b"].Status);
        Assert.False(state.Sources["a"].Running);
        Assert.Equal(1, api.LogsOf("a").Opened);
    }

    [Fact]
    public async Task A_failed_stream_is_reported_and_marked_failed()
    {
        var api = LogsApi("a");
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        await using var logs = h.Session.OpenLogs();
        Assert.NotNull(logs);
        await api.LogsOf("a").WhenOpened().WaitAsync(Patience);

        api.LogsOf("a").End(new EngineException(501, "configured logging driver does not support reading"));

        var state = await WaitForLogs(logs, s => s.Sources["a"].Status == LogSourceStatus.Failed);
        Assert.Equal("configured logging driver does not support reading", state.Sources["a"].Error);
        Assert.Contains(h.Errors, e => e.What.Contains("Logs page", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_logs_session_is_cut_off_when_its_connection_is_replaced()
    {
        var first = LogsApi("a");
        await using var h = Harness.With(first, LogsApi("a"));
        await h.ConnectAsync();
        await using var logs = h.Session.OpenLogs();
        Assert.NotNull(logs);
        await first.LogsOf("a").WhenOpened().WaitAsync(Patience);

        h.Session.Dispatch(new ConnectRequested());
        var state = await WaitForLogs(logs, s => s.ConnectionLost);
        await h.WaitFor(s => s.Status == ConnectionStatus.Connected && h.Created.Count == 2);

        Assert.True(logs.IsCutOff);
        Assert.True(first.IsDisposed);
        Assert.Contains("Logs a tail=100 follow=True tty=False", first.Cancelled);
        Assert.Equal(LogSourceStatus.Ended, state.Sources["a"].Status);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task A_closed_logs_session_stops_its_streams_and_hears_no_more_lists()
    {
        var api = LogsApi("a");
        await using var h = Harness.With(api);
        await h.ConnectAsync();
        var logs = h.Session.OpenLogs();
        Assert.NotNull(logs);
        await api.LogsOf("a").WhenOpened().WaitAsync(Patience);

        await logs.DisposeAsync();
        var before = logs.State;
        api.Containers = [FakeEngineApi.Container("a"), FakeEngineApi.Container("b")];
        h.Session.Dispatch(new ReloadRequested());
        await h.WaitFor(s => s.Containers.Length == 2);

        Assert.Contains("Logs a tail=100 follow=True tty=False", api.Cancelled);
        Assert.Same(before, logs.State);
        Assert.False(logs.IsCutOff);
        Assert.Empty(h.Errors);
    }
}

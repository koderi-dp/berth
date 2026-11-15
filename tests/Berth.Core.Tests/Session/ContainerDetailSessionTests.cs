using System.Collections.Concurrent;
using Berth.Engine;
using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using Microsoft.Extensions.Time.Testing;

namespace Berth.Tests.Session;

public sealed class ContainerDetailSessionTests
{
    /// <summary>Only a safety net so a broken runtime fails the test instead of hanging it; tests never wait for time.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);

    private const string LogsCall = "Logs c tail=1000 follow=True tty=False";

    private static LogLine Line(string text) => new(LogStream.Stdout, null, text);

    private static StatsSample Sample(int second) => new(T0.AddSeconds(second), 2.5, 1024, 4096, 0, 0, 0, 0, 1);

    [Fact]
    public async Task Open_reads_the_container_and_starts_both_streams()
    {
        await using var h = new Harness();

        h.Session.Open();
        var state = await h.WaitFor(s => s.Details is not null);
        await h.Api.Logs.WhenOpened().WaitAsync(Patience);
        await h.Api.Stats.WhenOpened().WaitAsync(Patience);

        Assert.Equal("c", state.Details!.Id);
        Assert.Equal(StreamStatus.Loading, state.LogStatus);
        Assert.Equal(StreamStatus.Loading, state.StatsStatus);
        Assert.Equal(["Inspect c", LogsCall, "Stats c"], h.Api.Calls.Order());
    }

    [Fact]
    public async Task A_flood_of_lines_is_applied_in_ticks_and_the_buffer_stays_capped()
    {
        await using var h = new Harness();
        await h.OpenAsync();

        const int Flood = 50_000;
        var collected = h.WaitForCollected(Flood);
        for (var i = 1; i <= Flood; i++)
            h.Api.Logs.Push(Line($"line {i}"));
        await collected;

        var changes = 0;
        h.Session.StateChanged += _ => Interlocked.Increment(ref changes);
        h.Time.Advance(Tick);
        var state = await h.WaitFor(s => s.Logs.Lines.Count > 0);

        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Equal(StreamStatus.Live, state.LogStatus);
        Assert.Equal(LogBuffer.Capacity, state.Logs.Lines.Count);
        Assert.Equal(Flood - LogBuffer.Capacity, state.Logs.Dropped);
        Assert.Equal($"line {Flood}", state.Logs.Lines[^1].Text);

        var more = h.WaitForCollected(1);
        h.Api.Logs.Push(Line("one more"));
        await more;
        h.Time.Advance(Tick);
        var next = await h.WaitFor(s => s.Logs.Lines[^1].Text == "one more");

        Assert.Equal(2, Volatile.Read(ref changes));
        Assert.Equal(LogBuffer.Capacity, next.Logs.Lines.Count);
    }

    [Fact]
    public async Task Lines_wait_for_the_tick()
    {
        await using var h = new Harness();
        await h.OpenAsync();

        var collected = h.WaitForCollected(1);
        h.Api.Logs.Push(Line("one"));
        await collected;
        h.Time.Advance(Tick - TimeSpan.FromMilliseconds(1));

        Assert.Empty(h.Session.State.Logs.Lines);

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        var state = await h.WaitFor(s => s.Logs.Lines.Count == 1);
        Assert.Equal("one", state.Logs.Lines[0].Text);
    }

    [Fact]
    public async Task Dispose_cancels_the_streams_and_raises_nothing_more()
    {
        var h = new Harness();
        await h.OpenAsync();

        await h.DisposeAsync();
        var raised = 0;
        h.Session.StateChanged += _ => Interlocked.Increment(ref raised);
        h.Session.Refresh();
        h.Api.Logs.Push(Line("late"));
        h.Api.Stats.Push(Sample(0));
        h.Time.Advance(Tick);

        Assert.Contains(LogsCall, h.Api.Cancelled);
        Assert.Contains("Stats c", h.Api.Cancelled);
        Assert.Equal(0, raised);
        Assert.False(h.Api.IsDisposed);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task A_stream_stopped_on_purpose_is_not_reported_as_a_failure()
    {
        await using var h = new Harness();
        await h.OpenAsync();

        var inspection = FakeEngineApi.Inspection("c");
        h.Api.SetInspection(inspection with
        {
            Details = inspection.Details with
            {
                State = inspection.Details.State with { StartedAt = inspection.Details.State.StartedAt!.Value.AddMinutes(1) },
            },
        });
        h.Session.Refresh();
        await h.Api.Logs.WhenOpened(2).WaitAsync(Patience);
        await h.Api.Stats.WhenOpened(2).WaitAsync(Patience);
        var state = await h.WaitFor(s => s.StreamGeneration == 2);

        Assert.Equal(StreamStatus.Loading, state.LogStatus);
        Assert.Equal(StreamStatus.Loading, state.StatsStatus);
        Assert.Null(state.LogError);
        Assert.Null(state.StatsError);

        // Disposing waits for every stream: the first pair was cancelled by the restart, the second by the dispose.
        await h.DisposeAsync();
        Assert.Equal(2, h.Api.Cancelled.Count(c => c == LogsCall));
        Assert.Equal(2, h.Api.Cancelled.Count(c => c == "Stats c"));
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task The_container_stopping_ends_stats_and_keeps_logs()
    {
        await using var h = new Harness();
        await h.OpenAsync();
        h.Api.Logs.Push(Line("one"));
        h.Api.Logs.Push(Line("two"));
        h.Api.Stats.Push(Sample(0));
        await h.WaitFor(s => s.StatsStatus == StreamStatus.Live);

        h.Api.SetInspection(FakeEngineApi.Inspection("c", running: false));
        h.Api.Stats.End();
        h.Api.Logs.End();
        var state = await h.WaitFor(s =>
            s.StatsStatus == StreamStatus.Ended && s.LogStatus == StreamStatus.Ended && s.Details is { State.Running: false });

        Assert.Equal(["one", "two"], state.Logs.Lines.Select(l => l.Text));
        Assert.Null(state.LogError);
        Assert.Null(state.StatsError);
        Assert.Equal(2.5, state.Stats.Latest?.CpuPercent);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task A_removed_container_is_marked_removed()
    {
        await using var h = new Harness();
        await h.OpenAsync();

        h.Api.RemoveInspection("c");
        h.Session.Refresh();
        var state = await h.WaitFor(s => s.Removed);

        Assert.Equal(StreamStatus.Ended, state.LogStatus);
        Assert.Equal(StreamStatus.Ended, state.StatsStatus);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task Clear_logs_empties_the_view()
    {
        await using var h = new Harness();
        await h.OpenAsync();
        h.Api.Logs.Push(Line("one"));
        h.Api.Logs.End();
        await h.WaitFor(s => s.Logs.Lines.Count == 1 && s.LogStatus == StreamStatus.Ended);

        h.Session.ClearLogs();
        var state = await h.WaitFor(s => s.Logs.Lines.Count == 0);

        Assert.Equal(2, state.Logs.FirstNumber);
    }

    [Fact]
    public async Task An_error_reaches_the_error_sink()
    {
        await using var h = new Harness();
        await h.OpenAsync();

        var broken = new EngineException(500, "log broke");
        h.Api.Logs.End(broken);
        var state = await h.WaitFor(s => s.LogStatus == StreamStatus.Failed);
        var error = await h.WaitForError();

        Assert.Equal("log broke", state.LogError);
        Assert.Equal("the container log stream failed", error.What);
        Assert.Same(broken, error.Exception);
    }

    [Fact]
    public async Task A_failed_inspect_is_reported_and_shown()
    {
        var api = new FakeEngineApi();
        var broken = new EngineException(500, "inspect broke");
        api.Fail("Inspect", broken);
        await using var h = new Harness(api);

        h.Session.Open();
        var state = await h.WaitFor(s => s.LoadError is not null);
        var error = await h.WaitForError();

        Assert.Equal("inspect broke", state.LoadError);
        Assert.Equal("reading the container failed", error.What);
        Assert.Same(broken, error.Exception);
    }

    /// <summary>Completes with the first state of <paramref name="session"/>, current or later, that matches <paramref name="match"/>.</summary>
    internal static async Task<DetailState> WaitFor(ContainerDetailSession session, Func<DetailState, bool> match)
    {
        var found = new TaskCompletionSource<DetailState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(DetailState s)
        {
            if (match(s))
                found.TrySetResult(s);
        }

        session.StateChanged += Check;
        try
        {
            Check(session.State);
            return await found.Task.WaitAsync(Patience);
        }
        finally
        {
            session.StateChanged -= Check;
        }
    }

    /// <summary>A detail session for container c over a fake engine, with a fake clock.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<(string What, Exception Exception)> _errors = new();
        private readonly SemaphoreSlim _errorArrived = new(0);

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
        }

        public FakeEngineApi Api { get; }

        public FakeTimeProvider Time { get; } = new();

        public ContainerDetailSession Session { get; }

        public IReadOnlyCollection<(string What, Exception Exception)> Errors => _errors;

        /// <summary>Opens the page and waits until both streams of the running container are open.</summary>
        public async Task OpenAsync()
        {
            Session.Open();
            await Api.Logs.WhenOpened().WaitAsync(Patience);
            await Api.Stats.WhenOpened().WaitAsync(Patience);
        }

        public Task<DetailState> WaitFor(Func<DetailState, bool> match) => ContainerDetailSessionTests.WaitFor(Session, match);

        /// <summary>Completes once the session holds <paramref name="count"/> log lines waiting for the next tick.</summary>
        public Task WaitForCollected(int count)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Check(int pending)
            {
                if (pending >= count)
                    done.TrySetResult();
            }

            Session.LinesCollected += Check;
            return done.Task.WaitAsync(Patience).ContinueWith(
                t =>
                {
                    Session.LinesCollected -= Check;
                    return t;
                },
                TaskScheduler.Default).Unwrap();
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
    }
}

using System.Collections.Concurrent;
using System.ComponentModel;
using Berth.Engine;
using Berth.Session;
using Microsoft.Extensions.Time.Testing;

namespace Berth.Tests.Session;

public sealed class EngineSessionEngineControlTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>An engine control whose commands wait until the test answers them.</summary>
    private sealed class FakeEngineControl : IEngineControl
    {
        public ConcurrentQueue<EngineCommand> Ran { get; } = new();

        public TaskCompletionSource<EngineCommandResult> Answer { get; private set; } = NewAnswer();

        public TaskCompletionSource<CancellationToken> Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<EngineCommandResult> RunAsync(EngineCommand command, CancellationToken cancellationToken)
        {
            Ran.Enqueue(command);
            var answer = Answer;
            Entered.TrySetResult(cancellationToken);
            await using var _ = cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken));
            return await answer.Task;
        }

        public void Reset()
        {
            Answer = NewAnswer();
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static TaskCompletionSource<EngineCommandResult> NewAnswer() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Func<IEngineApi> createApi, IEngineControl? control)
        {
            Session = new EngineSession(createApi, Time, (what, ex) => Errors.Enqueue((what, ex)), control);
        }

        public FakeTimeProvider Time { get; } = new();

        public EngineSession Session { get; }

        public ConcurrentQueue<(string What, Exception Exception)> Errors { get; } = new();

        public async Task<EngineState> WaitFor(Func<EngineState, bool> match)
        {
            var found = new TaskCompletionSource<EngineState>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Check(EngineState s)
            {
                if (match(s))
                    found.TrySetResult(s);
            }

            Session.StateChanged += Check;
            try
            {
                Check(Session.State);
                return await found.Task.WaitAsync(Patience);
            }
            finally
            {
                Session.StateChanged -= Check;
            }
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private static FakeEngineApi Refusing()
    {
        var api = new FakeEngineApi();
        api.Fail("Connect", new EngineException(0, "connection refused"));
        return api;
    }

    [Fact]
    public void A_session_without_engine_control_says_so()
    {
        var session = new EngineSession(() => new FakeEngineApi(), new FakeTimeProvider(), null, null);

        Assert.False(session.State.EngineControl.Available);
    }

    [Fact]
    public async Task Start_runs_the_command_then_connects()
    {
        var control = new FakeEngineControl();
        var apis = new ConcurrentQueue<FakeEngineApi>([Refusing()]);
        await using var h = new Harness(() => apis.TryDequeue(out var api) ? api : new FakeEngineApi(), control);
        Assert.True(h.Session.State.EngineControl.Available);

        h.Session.Dispatch(new ConnectRequested());
        await h.WaitFor(s => s.Status == ConnectionStatus.Disconnected && s.ConnectionError is not null);

        h.Session.Dispatch(new EngineCommandRequested(EngineCommand.Start));
        await control.Entered.Task.WaitAsync(Patience);
        Assert.Equal(EngineCommand.Start, h.Session.State.EngineControl.Running);

        control.Answer.SetResult(new EngineCommandResult(0, ""));
        var state = await h.WaitFor(s => s.Status == ConnectionStatus.Connected);

        Assert.Equal([EngineCommand.Start], control.Ran);
        Assert.Null(state.EngineControl.Running);
        Assert.Null(state.EngineControl.Failure);
    }

    [Fact]
    public async Task Stop_runs_the_command_and_leaves_the_engine_stopped_without_an_error()
    {
        var control = new FakeEngineControl();
        await using var h = new Harness(() => new FakeEngineApi(), control);
        h.Session.Dispatch(new ConnectRequested());
        await h.WaitFor(s => s.Status == ConnectionStatus.Connected);

        h.Session.Dispatch(new EngineCommandRequested(EngineCommand.Stop));
        await control.Entered.Task.WaitAsync(Patience);
        control.Answer.SetResult(new EngineCommandResult(0, ""));
        var state = await h.WaitFor(s => s.EngineControl.StoppedByBerth);

        Assert.Equal(ConnectionStatus.Disconnected, state.Status);
        Assert.Null(state.ConnectionError);
    }

    [Fact]
    public async Task A_command_that_runs_too_long_is_cancelled_and_reported_as_timed_out()
    {
        var control = new FakeEngineControl();
        await using var h = new Harness(Refusing, control);

        h.Session.Dispatch(new EngineCommandRequested(EngineCommand.Start));
        var token = await control.Entered.Task.WaitAsync(Patience);

        h.Time.Advance(SessionReducer.EngineCommandTimeout);
        var state = await h.WaitFor(s => s.EngineControl.Failure is not null);

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(EngineCommandFailureKind.TimedOut, state.EngineControl.Failure!.Kind);
        Assert.Null(state.EngineControl.Running);
    }

    [Fact]
    public async Task A_program_that_cannot_start_is_a_failure_with_its_reason_and_is_reported()
    {
        var control = new FakeEngineControl();
        await using var h = new Harness(Refusing, control);

        h.Session.Dispatch(new EngineCommandRequested(EngineCommand.Stop));
        await control.Entered.Task.WaitAsync(Patience);
        control.Answer.SetException(new Win32Exception(2, "The system cannot find the file specified."));
        var state = await h.WaitFor(s => s.EngineControl.Failure is not null);

        Assert.Equal(EngineCommandFailureKind.NotStarted, state.EngineControl.Failure!.Kind);
        Assert.Equal("The system cannot find the file specified.", state.EngineControl.Failure.Output);
        Assert.Contains(h.Errors, e => e.What == "an engine command could not be run" && e.Exception is Win32Exception);
    }

    [Fact]
    public async Task Disposing_the_session_ends_a_command_still_running_and_reports_nothing()
    {
        var control = new FakeEngineControl();
        var h = new Harness(Refusing, control);

        h.Session.Dispatch(new EngineCommandRequested(EngineCommand.Start));
        var token = await control.Entered.Task.WaitAsync(Patience);
        await h.DisposeAsync().AsTask().WaitAsync(Patience);

        Assert.True(token.IsCancellationRequested);
        Assert.DoesNotContain(h.Errors, e => e.What.Contains("engine command", StringComparison.Ordinal));
    }
}

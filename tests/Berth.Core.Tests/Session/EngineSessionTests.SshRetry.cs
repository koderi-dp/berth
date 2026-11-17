using Berth.Connections;
using Berth.Engine;
using Berth.Session;

namespace Berth.Tests.Session;

public sealed partial class EngineSessionTests
{
    /// <summary>
    /// A refusal that reaches the session only after the connection was already lost (its event is then stale) must still keep the
    /// session's own retry from signing in again: the retry fails at once with the refusal, and no new transport is made.
    /// </summary>
    [Fact]
    public async Task A_retry_after_a_refusal_does_not_reach_the_host_again()
    {
        var problem = new SshProblem(SshFailure.SignInRefused, "Permission denied (password).") { Secret = SecretKind.Password };
        var api = new FakeEngineApi();
        var stop = api.HoldPastCancellation("Stop");
        api.Fail("Stop", new EngineException(0, problem.Message) { Ssh = problem });
        await using var h = Harness.With(api, new FakeEngineApi());
        await h.ConnectAsync();

        h.Session.Dispatch(new ActionRequested(["a"], ContainerAction.Stop));
        await stop.Entered.WaitAsync(Patience);
        // The connection drops first, plainly: a retry is scheduled.
        api.EndStream(new EngineException(0, "The SSH connection was lost."));
        await h.WaitFor(s => s.Status == ConnectionStatus.Disconnected);
        // Then the refused sign-in under the action ends; its event belongs to the retired connection.
        stop.Release();
        await Task.Delay(200);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        var asking = await h.WaitFor(s => s.Attention is not null);

        Assert.Same(problem, asking.Attention);
        Assert.Single(h.Created);

        // The user's own Connect opens the gate again.
        h.Session.Dispatch(new ConnectRequested());
        await h.WaitFor(s => s.Status == ConnectionStatus.Connected);
        Assert.Equal(2, h.Created.Count);
    }
}

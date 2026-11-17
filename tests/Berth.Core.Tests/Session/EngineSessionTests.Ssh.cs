using Berth.Connections;
using Berth.Engine;
using Berth.Session;

namespace Berth.Tests.Session;

public sealed partial class EngineSessionTests
{
    [Fact]
    public async Task A_refused_password_is_tried_once_and_the_session_waits_for_the_user()
    {
        var problem = new SshProblem(SshFailure.SignInRefused, "Permission denied (password).") { Secret = SecretKind.Password };
        var refused = new FakeEngineApi();
        refused.Fail("Connect", new EngineException(0, problem.Message) { Ssh = problem });
        var good = new FakeEngineApi();
        await using var h = Harness.With(refused, good);

        h.Session.Dispatch(new ConnectRequested());
        var asking = await h.WaitFor(s => s.Attention is not null);

        // Far past every back-off delay: nothing connects on its own.
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(100);
        Assert.Single(h.Created);
        Assert.Equal(ConnectionStatus.Disconnected, h.Session.State.Status);
        Assert.Same(problem, asking.Attention);

        // The user entered the password: the app asks to connect again.
        h.Session.Dispatch(new ConnectRequested());
        var state = await h.WaitFor(s => s.Status == ConnectionStatus.Connected);

        Assert.Null(state.Attention);
        Assert.Equal(2, h.Created.Count);
    }
}

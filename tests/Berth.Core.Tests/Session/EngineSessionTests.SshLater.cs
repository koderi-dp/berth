using Berth.Connections;
using Berth.Engine;
using Berth.Session;

namespace Berth.Tests.Session;

public sealed partial class EngineSessionTests
{
    [Fact]
    public async Task A_refusal_on_a_later_ssh_session_ends_the_connection_and_asks_without_retrying()
    {
        var problem = new SshProblem(SshFailure.SignInRefused, "Permission denied (password).") { Secret = SecretKind.Password };
        var api = new FakeEngineApi();
        await using var h = Harness.With(api, new FakeEngineApi());
        await h.ConnectAsync();

        // The transport ends every stream with the refusal once a later session's sign-in was refused.
        api.EndStream(new EngineException(0, problem.Message) { Ssh = problem });
        var asking = await h.WaitFor(s => s.Attention is not null);

        h.Time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(100);
        Assert.Same(problem, asking.Attention);
        Assert.Equal(ConnectionStatus.Disconnected, h.Session.State.Status);
        Assert.Single(h.Created);
    }
}

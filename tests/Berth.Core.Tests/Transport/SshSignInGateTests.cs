using Berth.Connections;
using Berth.Transport;

namespace Berth.Tests.Transport;

public sealed class SshSignInGateTests
{
    private static readonly SshProblem Refused = new(SshFailure.SignInRefused, "Permission denied (password).") { Secret = SecretKind.Password };

    [Fact]
    public void An_open_gate_lets_a_sign_in_through()
    {
        var gate = new SshSignInGate();

        gate.ThrowIfClosed();

        Assert.Null(gate.Stopped);
    }

    [Fact]
    public void After_a_refusal_every_later_sign_in_fails_at_once_with_the_same_refusal()
    {
        var gate = new SshSignInGate();

        Assert.True(gate.Close(Refused));

        for (var i = 0; i < 5; i++)
        {
            var ex = Assert.Throws<SshTransportException>(gate.ThrowIfClosed);
            Assert.Same(Refused, ex.Problem);
        }
    }

    [Fact]
    public void Only_the_first_problem_that_needs_the_user_closes_the_gate()
    {
        var gate = new SshSignInGate();

        Assert.False(gate.Close(new SshProblem(SshFailure.Unreachable, "timed out")));
        Assert.Null(gate.Stopped);
        Assert.True(gate.Close(Refused));
        Assert.False(gate.Close(new SshProblem(SshFailure.SecretMissing, "") { Secret = SecretKind.Password }));

        Assert.Same(Refused, gate.Stopped);
    }
}

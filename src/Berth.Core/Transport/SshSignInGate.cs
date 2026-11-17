using Berth.Connections;

namespace Berth.Transport;

/// <summary>
/// Remembers the first sign-in problem that needs the user (a refused password or passphrase, a missing secret, a changed key) for one
/// SSH transport. From then on no session is signed in again with the secret the transport holds: every later attempt fails at once
/// with the same problem, without reaching the host. Three refused passwords can lock an account. Only a new transport (a connect the
/// user asked for, or after a new secret) starts with an open gate.
/// </summary>
public sealed class SshSignInGate
{
    private SshProblem? _stopped;

    /// <summary>The problem that closed the gate, or null while it is open.</summary>
    public SshProblem? Stopped => Volatile.Read(ref _stopped);

    /// <summary>Throws the problem that closed the gate, if it is closed.</summary>
    public void ThrowIfClosed()
    {
        if (Stopped is { } problem)
            throw new SshTransportException(problem);
    }

    /// <summary>Closes the gate on <paramref name="problem"/> when it needs the user and the gate is open; true when it did.</summary>
    public bool Close(SshProblem problem) =>
        problem.StopsRetry && Interlocked.CompareExchange(ref _stopped, problem, null) is null;
}

using Berth.Connections;

namespace Berth.Transport;

/// <summary>
/// An SSH connection or stream that failed for a reason the session and the user tell apart (<see cref="SshProblem"/>). An
/// <see cref="IOException"/>, so the engine client treats it as the transport failure it is; its message is the problem's own words.
/// </summary>
public sealed class SshTransportException(SshProblem problem, Exception? inner = null) : IOException(problem.Message, inner)
{
    public SshProblem Problem { get; } = problem;
}

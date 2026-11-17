using Berth.Connections;

namespace Berth.Engine;

/// <summary>
/// A failed engine call. <see cref="Exception.Message"/> is the engine's own message, shown to the user as-is.
/// <see cref="StatusCode"/> is the HTTP status, or 0 when the engine was not reached or did not answer in time.
/// </summary>
public sealed class EngineException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>The engine was reached but did not answer within the call's deadline (<see cref="StatusCode"/> is 0).</summary>
    public bool TimedOut { get; init; }

    /// <summary>Why an SSH connection failed, when it did (<see cref="StatusCode"/> is 0); null for every other failure.</summary>
    public SshProblem? Ssh { get; init; }
}

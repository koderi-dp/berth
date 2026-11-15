namespace Berth.Engine;

/// <summary>
/// A failed engine call. <see cref="Exception.Message"/> is the engine's own message, shown to the user as-is.
/// <see cref="StatusCode"/> is the HTTP status, or 0 when the engine was not reached or did not answer in time.
/// </summary>
public sealed class EngineException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

namespace Berth.Session;

/// <summary>The user asked to start or stop the engine (a stop was already confirmed).</summary>
public sealed record EngineCommandRequested(EngineCommand Command) : SessionEvent;

/// <summary>The command exited with <paramref name="ExitCode"/>; <paramref name="Output"/> is the end of what it wrote, never logged.</summary>
public sealed record EngineCommandExited(EngineCommand Command, int ExitCode, string Output) : SessionEvent;

/// <summary>
/// The session replaces one for the same connection, edited without a change of address, whose engine Berth had stopped: the stop
/// still holds, so the new session does not connect until Start engine or Retry. Ignored without engine control or once connecting.
/// </summary>
public sealed record EngineStoppedCarriedOver : SessionEvent;

/// <summary>The command's program could not be started; <paramref name="Message"/> says why, as Windows said it.</summary>
public sealed record EngineCommandNotStarted(EngineCommand Command, string Message) : SessionEvent;

/// <summary>The command did not exit in time and was ended with every process it started.</summary>
public sealed record EngineCommandTimedOut(EngineCommand Command) : SessionEvent;

/// <summary>
/// Run the connection's start or stop command; answers with EngineCommandExited, EngineCommandNotStarted or EngineCommandTimedOut.
/// </summary>
public sealed record RunEngineCommandEffect(EngineCommand Command) : SessionEffect;

/// <summary>How a command ended: its exit code and the end of what it wrote.</summary>
public sealed record EngineCommandResult(int ExitCode, string Output);

/// <summary>
/// Runs the connection's engine control commands (the app gives the session one when the connection has them). Throws when the program
/// cannot be started; when the token is cancelled ends the command with every process it started and throws
/// <see cref="OperationCanceledException"/>.
/// </summary>
public interface IEngineControl
{
    Task<EngineCommandResult> RunAsync(EngineCommand command, CancellationToken cancellationToken);
}

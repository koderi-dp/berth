namespace Berth.Session;

/// <summary>One of a connection's two engine control commands.</summary>
public enum EngineCommand { Start, Stop }

/// <summary>How an engine control command failed.</summary>
public enum EngineCommandFailureKind
{
    /// <summary>The program could not be started (not found, access denied).</summary>
    NotStarted,

    /// <summary>It ran and exited with a code other than 0.</summary>
    ExitCode,

    /// <summary>It did not exit within <see cref="SessionReducer.EngineCommandTimeout"/>; it and every process it started were ended.</summary>
    TimedOut,
}

/// <summary>An engine control command that failed.</summary>
/// <param name="ExitCode">The exit code, for <see cref="EngineCommandFailureKind.ExitCode"/>; 0 otherwise.</param>
/// <param name="Output">
/// The last lines the command wrote, as it wrote them (for <see cref="EngineCommandFailureKind.NotStarted"/>, why it could not start).
/// Never logged.
/// </param>
public sealed record EngineCommandFailure(EngineCommand Command, EngineCommandFailureKind Kind, int ExitCode, string Output)
{
    /// <summary>What failed, in a sentence, then the output as the command wrote it.</summary>
    public string Message
    {
        get
        {
            var name = Command == EngineCommand.Start ? "start" : "stop";
            var what = Kind switch
            {
                EngineCommandFailureKind.NotStarted => $"The {name} command could not be run.",
                EngineCommandFailureKind.TimedOut => $"The {name} command did not finish within {SessionReducer.EngineCommandTimeout.TotalSeconds:0} seconds and was ended.",
                _ => $"The {name} command failed with exit code {ExitCode}.",
            };
            return Output.Length == 0 ? what : what + "\n" + Output;
        }
    }
}

/// <summary>Starting and stopping the connection's engine from Berth.</summary>
/// <param name="Available">The connection has engine control commands.</param>
/// <param name="Running">The command running now; one at a time.</param>
/// <param name="Failure">The last command that failed, until the next one is asked for or the engine is reached.</param>
/// <param name="StoppedByBerth">
/// The stop command succeeded: the engine is down on purpose, so it is not shown as a connection error and Berth does not reconnect
/// (which could start a socket-activated engine again) until Start engine or Retry.
/// </param>
/// <param name="StartWaitsLeft">After a start, how many more quick reconnects are made while the engine comes up.</param>
public sealed record EngineControlState(bool Available, EngineCommand? Running, EngineCommandFailure? Failure, bool StoppedByBerth, int StartWaitsLeft)
{
    /// <summary>The connection has no engine control.</summary>
    public static readonly EngineControlState None = new(Available: false, Running: null, Failure: null, StoppedByBerth: false, StartWaitsLeft: 0);

    /// <summary>The connection has engine control and nothing has run yet.</summary>
    public static readonly EngineControlState Ready = None with { Available = true };
}

using Berth.Connections;
using Berth.Files;

namespace Berth.Session;

/// <summary>Start a download or an upload. Ignored while not connected.</summary>
public sealed record TransferRequested(FileTransferRequest Request) : SessionEvent;

/// <summary>A running transfer's bytes so far and its total once known (sent at most ten times a second).</summary>
public sealed record TransferProgressed(int Id, long Done, long? Total) : SessionEvent;

public sealed record TransferFinished(int Id, TransferSummary Summary) : SessionEvent;

/// <param name="EngineUnreachable">The engine could not be reached: the connection is lost too.</param>
public sealed record TransferFailed(int Id, string Message, bool EngineUnreachable) : SessionEvent
{
    /// <summary>Why an SSH connection failed under the call; null for other connections and failures.</summary>
    public SshProblem? Ssh { get; init; }
}

/// <summary>An upload found <see cref="Names"/> in the folder that the user has not agreed to replace; it waits for an answer.</summary>
public sealed record TransferNeedsAnswer(int Id, System.Collections.Immutable.ImmutableArray<string> Names) : SessionEvent;

/// <summary>Stop a running transfer: a download leaves nothing behind; an upload keeps what the engine already unpacked.</summary>
public sealed record TransferCancelRequested(int Id) : SessionEvent;

/// <summary>Forget a transfer that has ended.</summary>
public sealed record TransferDismissed(int Id) : SessionEvent;

/// <summary>Run the transfer; answers with TransferProgressed, then TransferFinished or TransferFailed, all carrying <see cref="Id"/>.</summary>
public sealed record StartTransferEffect(int Id, FileTransferRequest Request) : SessionEffect;

/// <summary>Stop the transfer; it sends nothing more.</summary>
public sealed record CancelTransferEffect(int Id) : SessionEffect;

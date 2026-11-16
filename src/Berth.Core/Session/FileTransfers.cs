using System.Collections.Immutable;
using Berth.Files;

namespace Berth.Session;

public enum TransferStatus { Running, Done, Failed, Cancelled, Interrupted }

/// <summary>A download or upload the user asked for, in one container of the session's connection.</summary>
public abstract record FileTransferRequest(string ContainerId);

/// <summary>
/// Save <see cref="Sources"/> (files or folders, each with everything under it) into the local folder <see cref="Destination"/>.
/// <see cref="Replace"/>: the user said yes to replacing what is already there.
/// </summary>
public sealed record DownloadRequest(string ContainerId, ImmutableArray<ContainerPath> Sources, string Destination, bool Replace)
    : FileTransferRequest(ContainerId);

/// <summary>Put the local files or folders <see cref="Sources"/> into the container's folder <see cref="Destination"/>.</summary>
public sealed record UploadRequest(string ContainerId, ImmutableArray<string> Sources, ContainerPath Destination)
    : FileTransferRequest(ContainerId);

/// <summary>
/// A transfer, tracked as a pull is: the bytes done, the total when known, and how it ended (its summary, or the failure as it was
/// reported). It belongs to the session's connection: a switch of connection drops it, and a lost connection interrupts it.
/// </summary>
public sealed record FileTransfer(
    int Id, FileTransferRequest Request, TransferStatus Status, long Done, long? Total, TransferSummary? Summary, string? Error)
{
    public bool IsDownload => Request is DownloadRequest;
}

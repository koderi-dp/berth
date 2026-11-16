using System.Collections.Immutable;
using Berth.Files;

namespace Berth.Session;

/// <summary>Asking: an upload found same-named entries in the container and waits for the user's yes; nothing was sent.</summary>
public enum TransferStatus { Running, Done, Failed, Cancelled, Interrupted, Asking }

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
    : FileTransferRequest(ContainerId)
{
    /// <summary>The names the user agreed to replace; any other name already in the folder makes the upload ask (<see cref="UploadCheck"/>).</summary>
    public ImmutableArray<string> Replace { get; init; } = [];
}

/// <summary>
/// A transfer, tracked as a pull is: the bytes done, the total when known, and how it ended (its summary, or the failure as it was
/// reported). It belongs to the session's connection: a switch of connection drops it, and a lost connection interrupts it.
/// </summary>
public sealed record FileTransfer(
    int Id, FileTransferRequest Request, TransferStatus Status, long Done, long? Total, TransferSummary? Summary, string? Error)
{
    public bool IsDownload => Request is DownloadRequest;

    /// <summary>While <see cref="TransferStatus.Asking"/>: the names already in the folder that the user has not agreed to replace.</summary>
    public ImmutableArray<string> Asks { get; init; } = [];
}

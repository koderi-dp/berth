using Berth.Engine.Models;

namespace Berth.Engine;

/// <summary>
/// The engine calls Berth makes. <see cref="ConnectAsync"/> must succeed before any other call. Failures throw
/// <see cref="EngineException"/>; cancelling the caller's token throws <see cref="OperationCanceledException"/>.
/// </summary>
public interface IEngineApi : IAsyncDisposable
{
    /// <summary>Reads <c>/version</c> and fixes the API version for later calls: the lower of the engine's and 1.56.</summary>
    Task<EngineVersion> ConnectAsync(CancellationToken ct);

    Task<EngineInfo> GetInfoAsync(CancellationToken ct);

    /// <summary>Lists every container, running or not.</summary>
    Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(CancellationToken ct);

    Task StartContainerAsync(string id, CancellationToken ct);

    Task StopContainerAsync(string id, CancellationToken ct);

    Task RestartContainerAsync(string id, CancellationToken ct);

    Task PauseContainerAsync(string id, CancellationToken ct);

    Task UnpauseContainerAsync(string id, CancellationToken ct);

    Task RemoveContainerAsync(string id, bool force, CancellationToken ct);

    /// <summary>Inspects one container. A container that does not exist throws the engine's 404.</summary>
    Task<ContainerInspection> InspectContainerAsync(string id, CancellationToken ct);

    /// <summary>
    /// A container's log lines, stdout and stderr, read with no deadline. <paramref name="tty"/> is the container's
    /// <see cref="ContainerDetails.Tty"/>: a TTY container's log is plain text rather than framed. With
    /// <see cref="LogRequest.Follow"/> the enumeration goes on until the container stops or the caller cancels.
    /// </summary>
    IAsyncEnumerable<LogLine> StreamLogsAsync(string id, bool tty, LogRequest request, CancellationToken ct);

    /// <summary>
    /// A running container's resource use, read with no deadline: one sample about every second while it runs. The enumeration
    /// ends when the container stops.
    /// </summary>
    IAsyncEnumerable<StatsSample> StreamStatsAsync(string id, CancellationToken ct);

    /// <summary>The engine's event stream, until cancelled.</summary>
    IAsyncEnumerable<EngineEventMessage> StreamEventsAsync(CancellationToken ct);

    /// <summary>Lists the images (not their intermediate layers).</summary>
    Task<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken ct);

    /// <summary>Inspects one image by ID or name: its details and the engine's whole answer, indented. An image that does not exist throws the engine's 404.</summary>
    Task<ImageInspection> InspectImageAsync(string idOrName, CancellationToken ct);

    /// <summary>
    /// Removes an image, or one of its tags when it has several, never forced; the answer lists each tag removed and image deleted.
    /// </summary>
    Task<IReadOnlyList<ImageDeleteResult>> RemoveImageAsync(string idOrName, CancellationToken ct);

    /// <summary>
    /// Pulls an image, read with no deadline: one item per progress line the engine sends. A failure the engine reports in the
    /// stream throws <see cref="EngineException"/> with its message (or a fixed text when it sent none).
    /// <paramref name="reference"/> is split by <see cref="ImageReference.Parse"/> ("nginx" pulls nginx:latest).
    /// </summary>
    IAsyncEnumerable<PullProgress> PullImageAsync(string reference, CancellationToken ct);

    Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct);

    Task<VolumeSummary> CreateVolumeAsync(string name, CancellationToken ct);

    /// <summary>Removes a volume, never forced: the engine refuses one a container uses.</summary>
    Task RemoveVolumeAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct);

    Task RemoveNetworkAsync(string id, CancellationToken ct);

    /// <summary>The disk use of images and volumes, read with no deadline (the engine can take long on a large store).</summary>
    Task<DiskUsage> GetDiskUsageAsync(CancellationToken ct);

    /// <summary>Creates a container (it is not started) and returns its ID.</summary>
    Task<string> CreateContainerAsync(ContainerCreateRequest request, CancellationToken ct);

    /// <summary>
    /// Creates a TTY exec of <paramref name="command"/> in a running container, attached to stdin, stdout and stderr and sized to
    /// <paramref name="size"/>, and returns its ID. 30 s deadline. A paused or stopped container throws the engine's 409, a missing
    /// one its 404. An invalid <paramref name="size"/> throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    Task<string> CreateExecAsync(string containerId, IReadOnlyList<string> command, TerminalSize size, CancellationToken ct);

    /// <summary>
    /// Starts an exec on a connection the engine switches to a two-way raw stream, and returns that stream: what is written to it is
    /// the shell's input, what is read is its output, and a read returns 0 when the shell has ended. The caller owns the stream;
    /// disposing it closes the connection. The engine's answer has the 30 s deadline; the stream has none, and its reads and writes
    /// throw what the transport throws. An answer that does not switch the connection, or any error answer, throws
    /// <see cref="EngineException"/>. <see cref="EngineClient"/> returns an <see cref="ExecStream"/>, whose write side can be closed alone.
    /// </summary>
    Task<Stream> StartExecAsync(string execId, TerminalSize size, CancellationToken ct);

    /// <summary>Resizes a running exec's terminal. 30 s deadline.</summary>
    Task ResizeExecAsync(string execId, TerminalSize size, CancellationToken ct);

    /// <summary>Reads whether an exec still runs and, once it has ended, its exit code. 30 s deadline.</summary>
    Task<ExecInspection> InspectExecAsync(string execId, CancellationToken ct);
}

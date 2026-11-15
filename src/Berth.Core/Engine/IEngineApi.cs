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
}

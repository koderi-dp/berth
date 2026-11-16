using Berth.Engine.Models;
using Berth.State;

namespace Berth.Session;

/// <summary>Where a detail page's log or stats stream stands.</summary>
public enum StreamStatus { Idle, Loading, Live, Ended, Failed }

/// <summary>
/// Everything a container's detail page shows. Only <see cref="DetailReducer"/> produces new values.
/// </summary>
/// <param name="Details">The last inspect answer, or null before the first.</param>
/// <param name="RawJson">The engine's whole inspect answer, indented, or null before the first.</param>
/// <param name="LoadError">The last failed inspect, as the engine reported it; cleared by the next one that succeeds.</param>
/// <param name="Removed">The engine answered 404: the container no longer exists.</param>
/// <param name="LogError">Why the log stream failed, as reported, when <see cref="LogStatus"/> is Failed.</param>
/// <param name="StatsError">Why the stats stream failed, as reported, when <see cref="StatsStatus"/> is Failed.</param>
public sealed record DetailState(
    string ContainerId,
    ContainerDetails? Details,
    string? RawJson,
    string? LoadError,
    bool Removed,
    LogBuffer Logs,
    StreamStatus LogStatus,
    string? LogError,
    StatsHistory Stats,
    StreamStatus StatsStatus,
    string? StatsError)
{
    /// <summary>The generation of the latest inspect; answers to older ones are ignored. 0 before the page opened.</summary>
    public int InspectGeneration { get; init; }

    /// <summary>The generation of the current log and stats streams; their older results are ignored. 0 before they started.</summary>
    public int StreamGeneration { get; init; }

    /// <summary>
    /// The engine connection this page ran on was closed or replaced: its streams are stopped and nothing more is read. The
    /// page has to be opened again on the new connection.
    /// </summary>
    public bool ConnectionLost { get; init; }

    /// <summary>The latest inspect was asked for because a stream ended by itself, not by an open or a refresh.</summary>
    public bool ReadAfterStreamEnded { get; init; }

    /// <summary>A page for <paramref name="containerId"/> that has not opened yet.</summary>
    public static DetailState Initial(string containerId) => new(
        containerId,
        Details: null,
        RawJson: null,
        LoadError: null,
        Removed: false,
        Logs: LogBuffer.Empty,
        LogStatus: StreamStatus.Idle,
        LogError: null,
        Stats: StatsHistory.Empty,
        StatsStatus: StreamStatus.Idle,
        StatsError: null);
}

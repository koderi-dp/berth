using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.State;

namespace Berth.Session;

/// <summary>Where one container's log stream on the Logs page stands.</summary>
public enum LogSourceStatus
{
    /// <summary>Not followed yet: it waits for a free stream (see <see cref="LogsReducer.MaxStreams"/>).</summary>
    Idle,

    /// <summary>Its stream is open (or opening).</summary>
    Live,

    /// <summary>Its stream ended by itself: the container stopped (or was removed). Its lines stay.</summary>
    Ended,

    /// <summary>Its stream failed; <see cref="LogSource.Error"/> says why.</summary>
    Failed,
}

/// <summary>
/// One container on the Logs page: who it is, whether it runs, and its stream.
/// </summary>
/// <param name="Generation">The generation of its current or last stream; lines and ends of other ones are ignored. 0 before any.</param>
/// <param name="Running">The container runs (or is paused) as the last list said.</param>
/// <param name="Removed">The container is gone from the engine; its lines stay, marked gone.</param>
/// <param name="Created">When the container was created (Unix seconds): the newest are followed first.</param>
/// <param name="LastTimestamp">The newest timestamp of a line it sent: a stream started again reads from there.</param>
/// <param name="Since">The time the current stream was asked to start at; its lines at or before it were already shown.</param>
/// <param name="Error">Why the stream failed, as the engine or the connection said it.</param>
public sealed record LogSource(
    LogOrigin Origin,
    LogSourceStatus Status,
    int Generation,
    bool Running,
    bool Removed,
    long Created,
    DateTimeOffset? LastTimestamp,
    DateTimeOffset? Since,
    string? Error);

/// <summary>
/// Everything the Logs page shows: the merged lines of every container on the connection and each container's stream. Only
/// <see cref="LogsReducer"/> produces new values.
/// </summary>
/// <param name="Sources">The containers followed or once followed, by id.</param>
/// <param name="Log">Their lines, merged by time.</param>
/// <param name="NotFollowed">Running containers whose logs are not followed: more than <see cref="LogsReducer.MaxStreams"/> run.</param>
/// <param name="ContainersKnown">The engine's container list has been read, so no containers means none.</param>
/// <param name="EngineHasContainers">The engine has at least one container, running or not.</param>
public sealed record LogsState(
    ImmutableDictionary<string, LogSource> Sources,
    MergedLog Log,
    int NotFollowed,
    bool ContainersKnown,
    bool EngineHasContainers)
{
    /// <summary>The sequence number the next line gets.</summary>
    public long NextSequence { get; init; } = 1;

    /// <summary>The generation the next stream gets.</summary>
    public int NextGeneration { get; init; } = 1;

    /// <summary>The connection the page ran on was closed or replaced: nothing more is read. The page opens a new session.</summary>
    public bool ConnectionLost { get; init; }

    /// <summary>The last container list, in the engine's order.</summary>
    public ImmutableArray<ContainerSummary> Containers { get; init; } = [];

    public static readonly LogsState Initial = new(
        ImmutableDictionary<string, LogSource>.Empty.WithComparers(StringComparer.Ordinal),
        MergedLog.Empty,
        NotFollowed: 0,
        ContainersKnown: false,
        EngineHasContainers: false);
}

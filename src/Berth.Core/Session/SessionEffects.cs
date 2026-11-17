using System.Collections.Immutable;
using Berth.Text;

namespace Berth.Session;

/// <summary>Work the reducer asks the session runtime to carry out.</summary>
public abstract record SessionEffect;

/// <summary>Negotiate the API version and read <c>/info</c>; answers with ConnectSucceeded or ConnectFailed.</summary>
/// <param name="Retry">
/// The session's own retry timer asked for it, not the user (a Connect, a Retry, a new secret). A retry never signs in with a secret an
/// SSH host refused on this session: it fails at once with that refusal.
/// </param>
public sealed record ConnectEffect(bool Retry = false) : SessionEffect;

/// <summary>List containers and read <c>/info</c>; answers with ContainersLoaded or LoadFailed carrying the same generation.</summary>
public sealed record LoadContainersEffect(int Generation) : SessionEffect;

/// <summary>Follow <c>/events</c>, dispatching EngineEventReceived, and ConnectionLost when the stream ends.</summary>
public sealed record StartEventStreamEffect : SessionEffect;

/// <summary>Stop every stream started for the current connection.</summary>
public sealed record StopStreamsEffect : SessionEffect;

/// <summary>Dispatch RetryDue after <see cref="Delay"/>.</summary>
public sealed record ScheduleRetryEffect(TimeSpan Delay) : SessionEffect;

/// <summary>Dispatch ReloadDue after <see cref="Delay"/>.</summary>
public sealed record ScheduleReloadEffect(TimeSpan Delay) : SessionEffect;

/// <summary>Run one container action; answers with ActionSucceeded or ActionFailed.</summary>
public sealed record RunActionEffect(string Id, ContainerAction Action, bool Force) : SessionEffect;

/// <summary>
/// Follow the stats of exactly <see cref="ContainerIds"/> on the current connection, starting and stopping only the
/// difference from the set watched so far; empty stops all. Answers with one StatsBatchArrived a second.
/// </summary>
public sealed record WatchStatsEffect(ImmutableArray<string> ContainerIds) : SessionEffect
{
    /// <summary>Equal when the ids are the same, in the same order (an <see cref="ImmutableArray{T}"/> compares by reference).</summary>
    public bool Equals(WatchStatsEffect? other) =>
        other is not null && ContainerIds.SequenceEqual(other.ContainerIds, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var id in ContainerIds)
            hash.Add(id, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>Inspect one container for its <c>docker run</c> command; answers with RunCommandInspected or RunCommandFailed.</summary>
public sealed record InspectForRunCommandEffect(string Id, ShellKind Shell) : SessionEffect;

/// <summary>
/// Hand <see cref="Text"/> to the app for the clipboard (<see cref="EngineSession.CopyRequested"/>). It may hold environment values:
/// it is never logged.
/// </summary>
public sealed record CopyTextEffect(string Text) : SessionEffect;

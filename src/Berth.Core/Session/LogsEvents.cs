using System.Collections.Immutable;
using Berth.Engine.Models;

namespace Berth.Session;

/// <summary>Something that happened to the Logs page's session: a new container list or a stream's lines or end.</summary>
public abstract record LogsEvent;

/// <summary>The engine session's container list changed.</summary>
public sealed record LogsContainersChanged(ImmutableArray<ContainerSummary> Containers) : LogsEvent;

/// <summary>A line of the container <see cref="ContainerId"/>'s stream of <see cref="Generation"/>.</summary>
public readonly record struct IncomingLogLine(string ContainerId, int Generation, LogLine Line);

/// <summary>The lines of every stream collected since the last batch, in the order they arrived, and when the batch was sent.</summary>
public sealed record LogsLinesArrived(ImmutableArray<IncomingLogLine> Lines, DateTimeOffset Received) : LogsEvent;

/// <summary>A container's stream ended: by itself when <see cref="Error"/> is null, otherwise failed.</summary>
public sealed record LogsStreamEnded(string ContainerId, int Generation, string? Error) : LogsEvent;

/// <summary>The connection the session ran on was closed or replaced; the runtime has already stopped its streams.</summary>
public sealed record LogsConnectionLost : LogsEvent;

/// <summary>Work <see cref="LogsReducer"/> asks the Logs page's session to carry out.</summary>
public abstract record LogsEffect;

/// <summary>
/// Follow the container's log: answers with <see cref="LogsLinesArrived"/> lines, then <see cref="LogsStreamEnded"/>, all carrying
/// <see cref="Generation"/>.
/// </summary>
public sealed record FollowContainerLogEffect(string ContainerId, int Generation, LogRequest Request) : LogsEffect;

/// <summary>Stop the container's stream of <see cref="Generation"/>; what it still sends is ignored.</summary>
public sealed record StopContainerLogEffect(string ContainerId, int Generation) : LogsEffect;

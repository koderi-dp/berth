using System.Collections.Immutable;
using Berth.Engine.Models;

namespace Berth.Session;

/// <summary>Something that happened to a detail page: a user request, an engine call's result or a stream message.</summary>
public abstract record DetailEvent;

/// <summary>The page opened: read the container, then start its streams.</summary>
public sealed record DetailOpenRequested : DetailEvent;

/// <summary>Read the container again; its streams restart if it was restarted.</summary>
public sealed record DetailRefreshRequested : DetailEvent;

/// <summary>The user emptied the log view.</summary>
public sealed record LogsClearRequested : DetailEvent;

public sealed record InspectLoaded(int Generation, ContainerInspection Inspection) : DetailEvent;

/// <param name="NotFound">The engine answered 404: the container was removed.</param>
public sealed record InspectFailed(int Generation, string Message, bool NotFound) : DetailEvent;

/// <summary>The log lines collected since the last batch, oldest first.</summary>
public sealed record LogLinesArrived(int Generation, ImmutableArray<LogLine> Lines) : DetailEvent;

/// <summary>The log stream ended: by itself (the container stopped) when <see cref="Error"/> is null, otherwise failed.</summary>
public sealed record LogStreamEnded(int Generation, string? Error) : DetailEvent;

public sealed record StatsSampleArrived(int Generation, StatsSample Sample) : DetailEvent;

/// <summary>The stats stream ended: by itself (the container stopped) when <see cref="Error"/> is null, otherwise failed.</summary>
public sealed record StatsStreamEnded(int Generation, string? Error) : DetailEvent;

/// <summary>The engine connection the page ran on was closed or replaced; the runtime has already stopped its calls.</summary>
public sealed record DetailConnectionLost : DetailEvent;

/// <summary>Work <see cref="DetailReducer"/> asks the detail session runtime to carry out.</summary>
public abstract record DetailEffect;

/// <summary>Inspect the container; answers with InspectLoaded or InspectFailed carrying the same generation.</summary>
public sealed record InspectContainerEffect(int Generation) : DetailEffect;

/// <summary>
/// Follow the log; answers with LogLinesArrived batches, then LogStreamEnded, all carrying the same generation.
/// <paramref name="Tty"/> is the container's <see cref="ContainerDetails.Tty"/>.
/// </summary>
public sealed record FollowLogsEffect(int Generation, bool Tty, LogRequest Request) : DetailEffect;

/// <summary>Follow the stats; answers with StatsSampleArrived, then StatsStreamEnded, all carrying the same generation.</summary>
public sealed record FollowStatsEffect(int Generation) : DetailEffect;

/// <summary>Stop the log and stats streams started so far; what they still send is ignored.</summary>
public sealed record StopDetailStreamsEffect : DetailEffect;

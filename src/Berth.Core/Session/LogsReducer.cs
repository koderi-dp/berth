using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.State;

namespace Berth.Session;

/// <summary>
/// Turns (Logs page state, event) into (new state, effects). Pure: no I/O and no clock. An event that does not apply returns the same
/// state instance and no effects.
/// </summary>
/// <remarks>
/// Every running (or paused) container is followed, newest first, up to <see cref="MaxStreams"/> streams at once: the last
/// <see cref="Tail"/> lines, then new ones. A stream ends by itself when its container stops; its lines stay. A container that runs
/// again is followed again from its newest line shown (only on a new container list, never on the end of its stream, so a stream that
/// keeps ending cannot loop). A container gone from the list keeps its lines, marked removed.
/// </remarks>
public static class LogsReducer
{
    /// <summary>How many of each container's newest lines are read before following new ones.</summary>
    public const int Tail = 100;

    /// <summary>The most container streams open at once.</summary>
    public const int MaxStreams = 50;

    private const string ProjectLabel = "com.docker.compose.project";
    private const string ServiceLabel = "com.docker.compose.service";

    private static readonly ImmutableArray<LogsEffect> None = [];

    public static (LogsState State, ImmutableArray<LogsEffect> Effects) Reduce(LogsState state, LogsEvent e)
    {
        if (state.ConnectionLost)
            return (state, None);

        switch (e)
        {
            case LogsContainersChanged changed:
                return Sync(state, changed.Containers);

            case LogsLinesArrived batch when batch.Lines.Length > 0:
                return (Append(state, batch), None);

            case LogsStreamEnded end when state.Sources.TryGetValue(end.ContainerId, out var source)
                                          && source.Generation == end.Generation
                                          && source.Status == LogSourceStatus.Live:
            {
                var ended = end.Error is null
                    ? source with { Status = LogSourceStatus.Ended, Error = null }
                    : source with { Status = LogSourceStatus.Failed, Error = end.Error };
                // A free stream goes to a container still waiting for one, never back to this one.
                return Fill(state with { Sources = state.Sources.SetItem(end.ContainerId, ended) }, restart: false);
            }

            case LogsConnectionLost:
            {
                var sources = state.Sources;
                foreach (var (id, s) in state.Sources)
                {
                    if (s.Status == LogSourceStatus.Live)
                        sources = sources.SetItem(id, s with { Status = LogSourceStatus.Ended });
                }

                return (state with { Sources = sources, ConnectionLost = true }, None);
            }

            default:
                return (state, None);
        }
    }

    /// <summary>Whether the container is followed: it runs or is paused (a paused container's stream stays open).</summary>
    public static bool IsRunning(ContainerSummary container) => container.State is "running" or "paused";

    /// <summary>The container's name and compose labels, as the page shows them.</summary>
    public static LogOrigin Origin(ContainerSummary container)
    {
        var name = container.Names.Count > 0 ? container.Names[0].TrimStart('/') : container.Id[..Math.Min(12, container.Id.Length)];
        var project = Label(container, ProjectLabel);
        var service = project is null ? null : Label(container, ServiceLabel);
        return new LogOrigin(container.Id, name, project, service);
    }

    private static string? Label(ContainerSummary container, string key) =>
        container.Labels.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>Takes in a new container list: new running containers, ones that stopped or run again, and ones removed.</summary>
    private static (LogsState, ImmutableArray<LogsEffect>) Sync(LogsState state, ImmutableArray<ContainerSummary> containers)
    {
        var effects = ImmutableArray.CreateBuilder<LogsEffect>();
        var sources = state.Sources.ToBuilder();
        var listed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var container in containers)
        {
            listed.Add(container.Id);
            var running = IsRunning(container);
            if (sources.TryGetValue(container.Id, out var source))
            {
                var origin = Origin(container);
                sources[container.Id] = source with
                {
                    Origin = origin == source.Origin ? source.Origin : origin,
                    Running = running,
                    Removed = false,
                    Created = container.Created,
                };
            }
            else if (running)
            {
                sources[container.Id] = new LogSource(
                    Origin(container), LogSourceStatus.Idle, Generation: 0, Running: true, Removed: false, container.Created,
                    LastTimestamp: null, Since: null, Error: null);
            }
        }

        foreach (var (id, source) in state.Sources)
        {
            if (listed.Contains(id) || source.Removed)
                continue;
            if (source.Status == LogSourceStatus.Live)
                effects.Add(new StopContainerLogEffect(id, source.Generation));
            sources[id] = source with
            {
                Removed = true,
                Running = false,
                Status = source.Status is LogSourceStatus.Live or LogSourceStatus.Idle ? LogSourceStatus.Ended : source.Status,
            };
        }

        var next = state with
        {
            Sources = sources.ToImmutable(),
            Containers = containers,
            ContainersKnown = true,
            EngineHasContainers = !containers.IsEmpty,
        };
        var (filled, started) = Fill(next, restart: true);
        effects.AddRange(started);
        return (filled, effects.ToImmutable());
    }

    /// <summary>
    /// Starts streams for running containers that have none, newest first, while fewer than <see cref="MaxStreams"/> are open; with
    /// <paramref name="restart"/> (a new container list) also for those whose stream ended while they run again. Counts the rest.
    /// </summary>
    private static (LogsState, ImmutableArray<LogsEffect>) Fill(LogsState state, bool restart)
    {
        var open = state.Sources.Values.Count(s => s.Status == LogSourceStatus.Live);
        var waiting = state.Sources.Values
            .Where(s => s.Running && !s.Removed && (s.Status == LogSourceStatus.Idle
                || (restart && s.Status is LogSourceStatus.Ended or LogSourceStatus.Failed)))
            .OrderByDescending(s => s.Created)
            .ThenBy(s => s.Origin.ContainerId, StringComparer.Ordinal)
            .ToList();

        var effects = ImmutableArray.CreateBuilder<LogsEffect>();
        var sources = state.Sources;
        var generation = state.NextGeneration;
        var notFollowed = 0;
        foreach (var source in waiting)
        {
            if (open >= MaxStreams)
            {
                notFollowed++;
                continue;
            }

            // A container followed before reads on from its newest line shown; one never followed reads its tail.
            var request = source.LastTimestamp is { } last
                ? new LogRequest(Tail: -1, Follow: true) { Since = last }
                : new LogRequest(Tail, Follow: true);
            sources = sources.SetItem(source.Origin.ContainerId, source with
            {
                Status = LogSourceStatus.Live,
                Generation = generation,
                Since = request.Since,
                Error = null,
            });
            effects.Add(new FollowContainerLogEffect(source.Origin.ContainerId, generation, request));
            generation++;
            open++;
        }

        var next = state with { Sources = sources, NextGeneration = generation, NotFollowed = notFollowed };
        return (next == state ? state : next, effects.ToImmutable());
    }

    /// <summary>Adds the lines of the current streams, each stamped with its container and placed by its time.</summary>
    private static LogsState Append(LogsState state, LogsLinesArrived batch)
    {
        var entries = new List<LogEntry>(batch.Lines.Length);
        var sources = state.Sources;
        var sequence = state.NextSequence;
        foreach (var incoming in batch.Lines)
        {
            if (!sources.TryGetValue(incoming.ContainerId, out var source) || source.Generation != incoming.Generation)
                continue;

            var line = incoming.Line;
            // A stream started again from a time repeats the lines at that time, which are shown already.
            if (source.Since is { } since && line.Timestamp is { } stamp && stamp <= since)
                continue;

            entries.Add(new LogEntry(sequence++, source.Origin, line, line.Timestamp ?? batch.Received));
            if (line.Timestamp is { } time && (source.LastTimestamp is not { } last || time > last))
                sources = sources.SetItem(incoming.ContainerId, source with { LastTimestamp = time });
        }

        return entries.Count == 0
            ? state
            : state with { Sources = sources, Log = state.Log.Add(entries), NextSequence = sequence };
    }
}

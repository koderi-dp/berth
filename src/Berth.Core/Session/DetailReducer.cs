using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Presentation;

namespace Berth.Session;

/// <summary>
/// Turns (detail state, event) into (new state, effects). Pure: no I/O and no clock. An event that does not apply in the
/// current state returns the same state instance and no effects.
/// </summary>
public static class DetailReducer
{
    /// <summary>How many of the newest log lines the page reads before following new ones.</summary>
    public const int LogTail = 1000;

    /// <summary>The log request every detail page makes.</summary>
    public static readonly LogRequest LogsRequest = new(LogTail, Follow: true);

    private static readonly ImmutableArray<DetailEffect> None = [];

    public static (DetailState State, ImmutableArray<DetailEffect> Effects) Reduce(DetailState state, DetailEvent e)
    {
        var current = !state.ConnectionLost;

        switch (e)
        {
            case DetailOpenRequested when current && state.InspectGeneration == 0:
            case DetailRefreshRequested when current && state.InspectGeneration > 0 && !state.Removed:
                return Inspect(state);

            case LogsClearRequested when state.Logs.Lines.Count > 0:
                return (state with { Logs = state.Logs.Clear() }, None);

            case InspectLoaded loaded when current && loaded.Generation == state.InspectGeneration:
                return Loaded(state, loaded.Inspection);

            case InspectFailed failure when current && failure.Generation == state.InspectGeneration:
                return failure.NotFound
                    ? (EndStreams(state) with { Removed = true }, [new StopDetailStreamsEffect()])
                    : (state with { LoadError = failure.Message }, None);

            case LogLinesArrived batch when current && batch.Generation == state.StreamGeneration && batch.Lines.Length > 0:
                return (state with { Logs = state.Logs.Append(batch.Lines), LogStatus = StreamStatus.Live }, None);

            case LogStreamEnded end when current && end.Generation == state.StreamGeneration:
            {
                var next = end.Error is null
                    ? state with { LogStatus = StreamStatus.Ended }
                    : state with { LogStatus = StreamStatus.Failed, LogError = end.Error };
                return AfterStreamEnded(next, end.Error);
            }

            case StatsSampleArrived sample when current && sample.Generation == state.StreamGeneration:
                return (state with { Stats = state.Stats.Add(sample.Sample), StatsStatus = StreamStatus.Live }, None);

            case StatsStreamEnded end when current && end.Generation == state.StreamGeneration:
            {
                var next = end.Error is null
                    ? state with { StatsStatus = StreamStatus.Ended }
                    : state with { StatsStatus = StreamStatus.Failed, StatsError = end.Error };
                return AfterStreamEnded(next, end.Error);
            }

            case DetailConnectionLost when current:
                // The runtime has already stopped every call; the bumped generations drop what was still on its way.
                return (EndStreams(state) with { ConnectionLost = true, InspectGeneration = state.InspectGeneration + 1 }, None);

            default:
                return (state, None);
        }
    }

    private static (DetailState, ImmutableArray<DetailEffect>) Inspect(DetailState state)
    {
        var generation = state.InspectGeneration + 1;
        return (state with { InspectGeneration = generation }, [new InspectContainerEffect(generation)]);
    }

    /// <summary>
    /// Shows the new details. The streams start after the first read, and start over (the log read again from its tail) when
    /// the container was started again since the last one.
    /// </summary>
    private static (DetailState, ImmutableArray<DetailEffect>) Loaded(DetailState state, ContainerInspection inspection)
    {
        var details = inspection.Details;
        var next = state with { Details = details, RawJson = inspection.RawJson, LoadError = null };
        if (state.StreamGeneration == 0)
            return StartStreams(next, details, None);

        var restarted = details.State.Running && details.State.StartedAt != state.Details?.State.StartedAt;
        return restarted
            ? StartStreams(next with { Logs = LogBuffer.Empty }, details, [new StopDetailStreamsEffect()])
            : (next, None);
    }

    private static (DetailState, ImmutableArray<DetailEffect>) StartStreams(
        DetailState state, ContainerDetails details, ImmutableArray<DetailEffect> before)
    {
        var generation = state.StreamGeneration + 1;
        var running = details.State.Running;
        var effects = before.Add(new FollowLogsEffect(generation, details.Tty, LogsRequest));
        if (running)
            effects = effects.Add(new FollowStatsEffect(generation));

        return (
            state with
            {
                StreamGeneration = generation,
                LogStatus = StreamStatus.Loading,
                LogError = null,
                StatsStatus = running ? StreamStatus.Loading : StreamStatus.Idle,
                StatsError = null,
            },
            effects);
    }

    /// <summary>
    /// A stream of a running container that ends by itself means the container stopped: read it again so the page shows its
    /// new state (or that it was removed).
    /// </summary>
    private static (DetailState, ImmutableArray<DetailEffect>) AfterStreamEnded(DetailState state, string? error) =>
        error is null && state.Details is { State.Running: true } && !state.Removed
            ? Inspect(state)
            : (state, None);

    /// <summary>Marks streams still loading or live as ended and drops anything they still send.</summary>
    private static DetailState EndStreams(DetailState state) => state with
    {
        StreamGeneration = state.StreamGeneration + 1,
        LogStatus = Ended(state.LogStatus),
        StatsStatus = Ended(state.StatsStatus),
    };

    private static StreamStatus Ended(StreamStatus status) =>
        status is StreamStatus.Loading or StreamStatus.Live ? StreamStatus.Ended : status;
}

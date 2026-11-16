using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.State;

namespace Berth.Session;

/// <summary>
/// Turns (detail state, event) into (new state, effects). Pure: no I/O and no clock. An event that does not apply in the
/// current state returns the same state instance and no effects.
/// </summary>
public static partial class DetailReducer
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
                return Inspect(state, afterStreamEnded: false);

            case LogsClearRequested when state.Logs.Lines.Count > 0:
                return (state with { Logs = state.Logs.Clear() }, None);

            case InspectLoaded loaded when current && loaded.Generation == state.InspectGeneration:
                return Loaded(state, loaded.Inspection);

            case InspectFailed failure when current && failure.Generation == state.InspectGeneration:
            {
                if (!failure.NotFound)
                    return (state with { LoadError = failure.Message }, None);

                var (exec, stopExec) = EndExec(state.Exec, stop: true);
                return (EndStreams(state) with { Removed = true, Exec = exec }, [new StopDetailStreamsEffect(), .. stopExec]);
            }

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
                return (EndStreams(state) with
                {
                    ConnectionLost = true,
                    InspectGeneration = state.InspectGeneration + 1,
                    Exec = EndExec(state.Exec, stop: false).Exec,
                }, None);

            default:
                return ReduceExec(state, e);
        }
    }

    private static (DetailState, ImmutableArray<DetailEffect>) Inspect(DetailState state, bool afterStreamEnded)
    {
        var generation = state.InspectGeneration + 1;
        return (
            state with { InspectGeneration = generation, ReadAfterStreamEnded = afterStreamEnded },
            [new InspectContainerEffect(generation)]);
    }

    /// <summary>
    /// Shows the new details. The streams start after the first read, and start over (the log read again from its tail) when
    /// the container was started again since the last one, or when a refresh finds it running while a stream of it has ended
    /// (a restart the read after the stream ended saw only half of). The read that follows a stream ending never starts the
    /// streams for the second reason, so a stream that keeps ending cannot loop.
    /// </summary>
    private static (DetailState, ImmutableArray<DetailEffect>) Loaded(DetailState state, ContainerInspection inspection)
    {
        var details = inspection.Details;
        var next = state with { Details = details, RawJson = inspection.RawJson, LoadError = null };
        if (state.StreamGeneration == 0)
            return StartStreams(next, details, None);

        var restarted = details.State.Running && details.State.StartedAt != state.Details?.State.StartedAt;
        var streamEnded = state.LogStatus == StreamStatus.Ended || state.StatsStatus is StreamStatus.Ended or StreamStatus.Idle;
        var followAgain = !state.ReadAfterStreamEnded && IsRunning(details) && streamEnded;
        return restarted || followAgain
            ? StartStreams(next with { Logs = LogBuffer.Empty }, details, [new StopDetailStreamsEffect()])
            : (next, None);
    }

    /// <summary>Running and not waiting to start again after an exit (a restart policy's back-off reads as running and restarting).</summary>
    private static bool IsRunning(ContainerDetails details) => details.State is { Running: true, Restarting: false };

    private static (DetailState, ImmutableArray<DetailEffect>) StartStreams(
        DetailState state, ContainerDetails details, ImmutableArray<DetailEffect> before)
    {
        var generation = state.StreamGeneration + 1;
        var running = IsRunning(details);
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
            ? Inspect(state, afterStreamEnded: true)
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

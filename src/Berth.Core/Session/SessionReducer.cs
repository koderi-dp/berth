using System.Collections.Immutable;
using Berth.Engine.Models;

namespace Berth.Session;

/// <summary>
/// Turns (state, event) into (new state, effects). Pure: no I/O and no clock. An event that does not apply in the
/// current state returns the same state instance and no effects.
/// </summary>
public static class SessionReducer
{
    public static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(150);

    /// <summary>Container event actions that change what the list shows.</summary>
    private static readonly ImmutableHashSet<string> ReloadActions = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "create", "start", "die", "stop", "kill", "pause", "unpause", "restart", "rename", "destroy", "health_status");

    private static readonly ImmutableArray<SessionEffect> None = [];

    public static (EngineState State, ImmutableArray<SessionEffect> Effects) Reduce(EngineState state, SessionEvent e)
    {
        var connected = state.Status == ConnectionStatus.Connected;
        var connecting = state.Status == ConnectionStatus.Connecting;

        switch (e)
        {
            case ConnectRequested when state.Status == ConnectionStatus.Disconnected:
                return (state with { Status = ConnectionStatus.Connecting, RetryAttempt = 0, ListLoaded = false }, [new ConnectEffect()]);

            case ConnectRequested:
                // Starting over drops in-flight work: action results, the reload timer and the stats streams.
                return (
                    state with
                    {
                        Status = ConnectionStatus.Connecting,
                        Pending = state.Pending.Clear(),
                        RetryAttempt = 0,
                        LoadGeneration = state.LoadGeneration + 1,
                        ReloadScheduled = false,
                        ListLoaded = false,
                        Stats = state.Stats.Clear(),
                    },
                    [new StopStreamsEffect(), new ConnectEffect()]);

            case RetryDue when state.Status == ConnectionStatus.Disconnected:
                return (state with { Status = ConnectionStatus.Connecting }, [new ConnectEffect()]);

            case ConnectSucceeded success when connecting:
            {
                var generation = state.LoadGeneration + 1;
                return (
                    state with
                    {
                        Status = ConnectionStatus.Connected,
                        Version = success.Version,
                        Info = success.Info,
                        ConnectionError = null,
                        RetryAttempt = 0,
                        LoadGeneration = generation,
                        ListLoaded = false,
                    },
                    [new LoadContainersEffect(generation), new StartEventStreamEffect()]);
            }

            case ConnectFailed failure when connecting:
                return (
                    state with
                    {
                        Status = ConnectionStatus.Disconnected,
                        ConnectionError = failure.Message,
                        RetryAttempt = state.RetryAttempt + 1,
                    },
                    [new ScheduleRetryEffect(Backoff.Delay(state.RetryAttempt))]);

            case ConnectionLost lost when connected:
                return Lose(state, lost.Message);

            case ContainersLoaded loaded when connected && loaded.Generation == state.LoadGeneration:
            {
                var ids = loaded.Containers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                var gone = state.Pending.Keys.Where(id => !ids.Contains(id));
                var next = state with
                {
                    Containers = loaded.Containers,
                    Info = loaded.Info,
                    Pending = state.Pending.RemoveRange(gone),
                    ListLoaded = true,
                };
                return next.StatsWanted ? Watch(next) : (next, None);
            }

            case LoadFailed failure when connected && failure.Generation == state.LoadGeneration:
                return failure.EngineUnreachable
                    ? Lose(state, failure.Message)
                    : (state with { ActionError = failure.Message }, None);

            case EngineEventReceived received when connected && CausesReload(received.Message):
                return RequestReload(state);

            case ReloadDue:
            {
                // A reload may be due from before a disconnect; while not connected it only clears the flag.
                var cleared = state.ReloadScheduled ? state with { ReloadScheduled = false } : state;
                if (!connected)
                    return (cleared, None);
                var generation = state.LoadGeneration + 1;
                return (cleared with { LoadGeneration = generation }, [new LoadContainersEffect(generation)]);
            }

            case ReloadRequested when connected:
            {
                // At once, not debounced: the user is waiting. A load still in flight is superseded by the new generation.
                var generation = state.LoadGeneration + 1;
                return (state with { LoadGeneration = generation }, [new LoadContainersEffect(generation)]);
            }

            case ActionRequested request when connected:
                return RequestActions(state, request);

            case ActionSucceeded success when connected:
                return RequestReload(state with { Pending = state.Pending.Remove(success.Id) });

            case ActionFailed failure when connected:
                return failure.EngineUnreachable
                    ? Lose(state, failure.Message)
                    : RequestReload(state with { Pending = state.Pending.Remove(failure.Id), ActionError = failure.Message });

            case ActionErrorDismissed when state.ActionError is not null:
                return (state with { ActionError = null }, None);

            case StatsWantedChanged wanted when wanted.Wanted != state.StatsWanted:
            {
                var next = state with { StatsWanted = wanted.Wanted };
                if (!connected)
                    return (next with { Stats = next.Stats.Clear() }, None);
                if (!wanted.Wanted)
                    return (next with { Stats = next.Stats.Clear() }, [new WatchStatsEffect([])]);

                // Until the connection's first list arrives the running set is not known; that list starts the watch.
                return next.ListLoaded ? Watch(next) : (next, None);
            }

            case StatsBatchArrived batch when connected && state.StatsWanted:
            {
                var running = RunningIds(state);
                var fresh = batch.Latest.Where(sample => running.Contains(sample.Key)).ToList();
                return fresh.Count == 0 ? (state, None) : (state with { Stats = state.Stats.SetItems(fresh) }, None);
            }

            default:
                return (state, None);
        }
    }

    /// <summary>
    /// The connection is gone: keep the last list on screen, forget pending actions and any scheduled reload (its timer
    /// may be cancelled), ignore any load still in flight, and retry with back-off.
    /// </summary>
    private static (EngineState, ImmutableArray<SessionEffect>) Lose(EngineState state, string message) => (
        state with
        {
            Status = ConnectionStatus.Disconnected,
            ConnectionError = message,
            Pending = state.Pending.Clear(),
            RetryAttempt = state.RetryAttempt + 1,
            LoadGeneration = state.LoadGeneration + 1,
            ReloadScheduled = false,
            Stats = state.Stats.Clear(),
        },
        [new StopStreamsEffect(), new ScheduleRetryEffect(Backoff.Delay(state.RetryAttempt))]);

    /// <summary>Watches the stats of the running containers and forgets those of containers no longer running.</summary>
    private static (EngineState, ImmutableArray<SessionEffect>) Watch(EngineState state)
    {
        var running = RunningIds(state);
        var stale = state.Stats.Keys.Where(id => !running.Contains(id));
        var ids = state.Containers.Where(IsRunning).Select(c => c.Id).ToImmutableArray();
        return (state with { Stats = state.Stats.RemoveRange(stale) }, [new WatchStatsEffect(ids)]);
    }

    private static bool IsRunning(ContainerSummary container) => container.State == "running";

    private static HashSet<string> RunningIds(EngineState state) =>
        state.Containers.Where(IsRunning).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>Schedules a debounced reload unless one is already waiting.</summary>
    private static (EngineState, ImmutableArray<SessionEffect>) RequestReload(EngineState state) =>
        state.ReloadScheduled
            ? (state, None)
            : (state with { ReloadScheduled = true }, [new ScheduleReloadEffect(ReloadDebounce)]);

    private static (EngineState, ImmutableArray<SessionEffect>) RequestActions(EngineState state, ActionRequested request)
    {
        var pending = state.Pending.ToBuilder();
        var effects = ImmutableArray.CreateBuilder<SessionEffect>();
        foreach (var id in request.Ids)
        {
            if (pending.TryAdd(id, request.Action))
                effects.Add(new RunActionEffect(id, request.Action, request.Force));
        }

        return effects.Count == 0 ? (state, None) : (state with { Pending = pending.ToImmutable() }, effects.ToImmutable());
    }

    /// <summary>
    /// A container event whose action is in <see cref="ReloadActions"/>. The engine adds detail after a colon to some
    /// actions ("health_status: healthy"), so only the part before it is compared.
    /// </summary>
    private static bool CausesReload(EngineEventMessage message)
    {
        if (message.Type != "container")
            return false;
        var colon = message.Action.IndexOf(':');
        var action = colon < 0 ? message.Action : message.Action[..colon];
        return ReloadActions.Contains(action);
    }
}

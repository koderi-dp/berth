using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.State;
using Berth.Text;

namespace Berth.Session;

/// <summary>
/// Turns (state, event) into (new state, effects). Pure: no I/O and no clock. An event that does not apply in the
/// current state returns the same state instance and no effects.
/// </summary>
public static partial class SessionReducer
{
    public static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(150);

    /// <summary>What the error bar says when a copy to the clipboard failed.</summary>
    public const string ClipboardFailedText = "Could not copy to the clipboard. Try again.";

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
                return (
                    ForgetUnderWay(state, startingOver: true) with { Status = ConnectionStatus.Connecting, RetryAttempt = 0, ListLoaded = false },
                    [new ConnectEffect()]);

            case ConnectRequested:
                // Starting over drops in-flight work: action results, the reload timer and the stats streams.
                return (
                    ForgetUnderWay(state, startingOver: true) with
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
                        ContainersLoadError = null,
                    },
                    ImmutableArray.Create<SessionEffect>(new LoadContainersEffect(generation), new StartEventStreamEffect()))
                    .Then(LoadWanted);
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
                    ContainersLoadError = null,
                };
                return next.StatsWanted ? Watch(next) : (next, None);
            }

            case LoadFailed failure when connected && failure.Generation == state.LoadGeneration:
                return failure.EngineUnreachable
                    ? Lose(state, failure.Message)
                    : (state with { ActionError = failure.Message, ContainersLoadError = failure.Message }, None);

            case EngineEventReceived received when connected:
                return (CausesReload(received.Message) ? RequestReload(state) : (state, None)).Then(s => ReloadListsFor(s, received.Message));

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
                // A failed read's error stays until a read succeeds: what is unused is not known meanwhile.
                return (state with { LoadGeneration = generation }, ImmutableArray.Create<SessionEffect>(new LoadContainersEffect(generation)))
                    .Then(ReloadWantedLists);
            }

            case ActionRequested request when connected:
                return RequestActions(state, request);

            case ActionSucceeded success when connected:
                return RequestReload(state with { Pending = state.Pending.Remove(success.Id) });

            case ActionFailed failure when connected:
                return failure.EngineUnreachable
                    ? Lose(state, failure.Message)
                    : RequestReload(state with { Pending = state.Pending.Remove(failure.Id), ActionError = failure.Message });

            case RunCommandRequested request when connected:
                return (state, [new InspectForRunCommandEffect(request.Id, request.Shell)]);

            case RunCommandInspected inspected when connected:
                return (state, [new CopyTextEffect(DockerRunCommand.Build(inspected.Details, inspected.Shell))]);

            case RunCommandFailed failure when connected:
                return failure.EngineUnreachable
                    ? Lose(state, failure.Message)
                    : (state with { ActionError = failure.Message }, None);

            case ClipboardCopyFailed:
                return (state with { ActionError = ClipboardFailedText }, None);

            case ExternalTerminalFailed failure:
                return (state with { ActionError = failure.Message }, None);

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
                return ReduceResources(state, e, connected);
        }
    }

    /// <summary>
    /// The connection is gone: keep the last list on screen, forget pending actions and any scheduled reload (its timer
    /// may be cancelled), ignore any load still in flight, and retry with back-off.
    /// </summary>
    private static (EngineState, ImmutableArray<SessionEffect>) Lose(EngineState state, string message) => (
        ForgetUnderWay(state, startingOver: false) with
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
    /// A container event whose action is in <see cref="ReloadActions"/>, or a container joining or leaving a network. The engine adds
    /// detail after a colon to some actions ("health_status: healthy"), so only the part before it is compared.
    /// </summary>
    private static bool CausesReload(EngineEventMessage message) =>
        message.Type switch
        {
            "container" => ReloadActions.Contains(BaseAction(message.Action)),
            // Which networks a container is attached to comes from the container list.
            "network" => BaseAction(message.Action) is "connect" or "disconnect",
            _ => false,
        };

    /// <summary>An event's action without the detail the engine adds after a colon to some ("health_status: healthy").</summary>
    private static string BaseAction(string action)
    {
        var colon = action.IndexOf(':');
        return colon < 0 ? action : action[..colon];
    }
}

using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.State;

namespace Berth.Session;

/// <summary>The images, volumes and networks lists, their actions, disk use, image pulls and running a container.</summary>
public static partial class SessionReducer
{
    private static readonly ImmutableHashSet<string> ImageReloadActions =
        ImmutableHashSet.Create(StringComparer.Ordinal, "pull", "tag", "untag", "delete", "import", "load");

    private static readonly ImmutableHashSet<string> VolumeReloadActions =
        ImmutableHashSet.Create(StringComparer.Ordinal, "create", "destroy", "mount", "unmount");

    private static readonly ImmutableHashSet<string> NetworkReloadActions =
        ImmutableHashSet.Create(StringComparer.Ordinal, "create", "destroy", "connect", "disconnect");

    /// <summary>Container events that change which images and volumes are in use.</summary>
    private static readonly ImmutableHashSet<string> InUseActions = ImmutableHashSet.Create(StringComparer.Ordinal, "create", "destroy");

    private static readonly ResourceKind[] Kinds = [ResourceKind.Images, ResourceKind.Volumes, ResourceKind.Networks];

    private static (EngineState State, ImmutableArray<SessionEffect> Effects) ReduceResources(EngineState state, SessionEvent e, bool connected)
    {
        switch (e)
        {
            case ImagesWanted:
                return Want(state, ResourceKind.Images, connected);

            case VolumesWanted:
                return Want(state, ResourceKind.Volumes, connected);

            case NetworksWanted:
                return Want(state, ResourceKind.Networks, connected);

            case ImagesLoaded loaded when connected && loaded.Generation == state.Images.Status.Generation:
            {
                var keys = loaded.Images.SelectMany(i => i.RepoTags.Prepend(i.Id));
                var next = state with { Images = new(loaded.Images, Arrived(state.Images.Status, keys)) };
                var shown = next.Pulls.Values.Where(p => p.Status == PullStatus.Done && loaded.Generation > p.FinishedAtLoad).Select(p => p.Reference);
                return (next with { Pulls = next.Pulls.RemoveRange(shown) }, None);
            }

            case VolumesLoaded loaded when connected && loaded.Generation == state.Volumes.Status.Generation:
                return (state with { Volumes = new(loaded.Volumes, Arrived(state.Volumes.Status, loaded.Volumes.Select(v => v.Name))) }, None);

            case NetworksLoaded loaded when connected && loaded.Generation == state.Networks.Status.Generation:
                return (state with { Networks = new(loaded.Networks, Arrived(state.Networks.Status, loaded.Networks.Select(n => n.Id))) }, None);

            case ResourceLoadFailed failure when connected && failure.Generation == StatusOf(state, failure.Kind).Generation:
                return failure.EngineUnreachable ? Lose(state, failure.Message) : (state with { ActionError = failure.Message }, None);

            case ResourceReloadDue due:
            {
                var status = StatusOf(state, due.Kind);
                var cleared = status.ReloadScheduled ? WithStatus(state, due.Kind, status with { ReloadScheduled = false }) : state;
                return connected ? Load(cleared, due.Kind) : (cleared, None);
            }

            case RemoveImagesRequested request when connected:
                return RequestItems(state, ResourceKind.Images, request.References, ItemAction.Remove, request.Force);

            case RemoveVolumesRequested request when connected:
                return RequestItems(state, ResourceKind.Volumes, request.Names, ItemAction.Remove, force: false);

            case RemoveNetworksRequested request when connected:
                return RequestItems(state, ResourceKind.Networks, request.Ids, ItemAction.Remove, force: false);

            case CreateVolumeRequested request when connected:
                return RequestItems(state, ResourceKind.Volumes, [request.Name], ItemAction.Create, force: false);

            case ItemActionSucceeded success when connected:
            {
                var status = StatusOf(state, success.Kind);
                var removed = !status.Pending.TryGetValue(success.Key, out var action) || action == ItemAction.Remove;
                var next = WithStatus(state, success.Kind, status with { Pending = status.Pending.Remove(success.Key) });
                return removed ? RequestListReload(next, success.Kind).Then(ReloadAskedDiskUsage) : RequestListReload(next, success.Kind);
            }

            case ItemActionFailed failure when connected:
            {
                if (failure.EngineUnreachable)
                    return Lose(state, failure.Message);
                var status = StatusOf(state, failure.Kind);
                var next = WithStatus(state, failure.Kind, status with { Pending = status.Pending.Remove(failure.Key) });
                return RequestListReload(next with { ActionError = failure.Message }, failure.Kind);
            }

            case PruneRequested request when connected && !StatusOf(state, request.Kind).Pruning:
                return (
                    WithStatus(state, request.Kind, StatusOf(state, request.Kind) with { Pruning = true, Pruned = null }),
                    [new PruneEffect(request.Kind, request.DanglingImagesOnly)]);

            case PruneSucceeded success when connected && StatusOf(state, success.Kind).Pruning:
            {
                var next = WithStatus(state, success.Kind, StatusOf(state, success.Kind) with { Pruning = false, Pruned = success.Result });
                return RequestListReload(next, success.Kind).Then(ReloadAskedDiskUsage);
            }

            case PruneFailed failure when connected && StatusOf(state, failure.Kind).Pruning:
            {
                var next = WithStatus(state, failure.Kind, StatusOf(state, failure.Kind) with { Pruning = false });
                return failure.EngineUnreachable ? Lose(next, failure.Message) : (next with { ActionError = failure.Message }, None);
            }

            case DiskUsageRequested when connected && !state.DiskUsage.Loading:
                return LoadDiskUsage(state);

            case DiskUsageLoaded loaded when connected && state.DiskUsage.Loading && loaded.Generation == state.DiskUsage.Generation:
                return (state with { DiskUsage = state.DiskUsage with { Usage = loaded.Usage, Loading = false } }, None);

            case DiskUsageFailed failure when connected && state.DiskUsage.Loading && failure.Generation == state.DiskUsage.Generation:
            {
                var next = state with { DiskUsage = state.DiskUsage with { Loading = false } };
                return failure.EngineUnreachable ? Lose(next, failure.Message) : (next with { ActionError = failure.Message }, None);
            }

            case PullRequested request when connected:
                return StartPull(state, request.Reference);

            case PullProgressArrived progress when IsRunning(state, progress.Reference, progress.Generation, out var pull):
                return (state with { Pulls = state.Pulls.SetItem(pull.Reference, pull.With(progress.Lines)) }, None);

            case PullFinished finished when IsRunning(state, finished.Reference, finished.Generation, out var pull):
            {
                var done = pull with { Status = PullStatus.Done, FinishedAtLoad = state.Images.Status.Generation };
                return RequestListReload(state with { Pulls = state.Pulls.SetItem(pull.Reference, done) }, ResourceKind.Images);
            }

            case PullFailed failure when IsRunning(state, failure.Reference, failure.Generation, out var pull):
            {
                var failed = state with { Pulls = state.Pulls.SetItem(pull.Reference, pull with { Status = PullStatus.Failed, Error = failure.Message }) };
                return failure.EngineUnreachable ? Lose(failed, failure.Message) : (failed, None);
            }

            case PullCancelRequested cancel when state.Pulls.TryGetValue(PullKey(cancel.Reference), out var pull):
                return (
                    state with { Pulls = state.Pulls.Remove(pull.Reference) },
                    pull.Status == PullStatus.Running ? [new CancelPullEffect(pull.Reference)] : None);

            case PullDismissed dismissed when state.Pulls.TryGetValue(PullKey(dismissed.Reference), out var pull) && pull.Status != PullStatus.Running:
                return (state with { Pulls = state.Pulls.Remove(pull.Reference) }, None);

            case RunContainerRequested request when connected && state.Run?.Status != RunStatus.InProgress:
                return (state with { Run = new ContainerRun(request.Request.Image, RunStatus.InProgress, null, null) }, [new RunContainerEffect(request.Request)]);

            case RunSucceeded success when connected && state.Run is { Status: RunStatus.InProgress } run:
                return RequestReload(state with { Run = run with { Status = RunStatus.Started, ContainerId = success.ContainerId } });

            case RunFailed failure when connected && state.Run is { Status: RunStatus.InProgress } run:
            {
                var created = failure.CreatedId is not null;
                var next = state with
                {
                    Run = run with { Status = created ? RunStatus.StartFailed : RunStatus.Failed, ContainerId = failure.CreatedId, Error = failure.Message },
                };
                return failure.EngineUnreachable ? Lose(next, failure.Message)
                    : created ? RequestReload(next)
                    : (next, None);
            }

            case RunDismissed when state.Run is { Status: not RunStatus.InProgress }:
                return (state with { Run = null }, None);

            default:
                return (state, None);
        }
    }

    /// <summary>
    /// What is under way on the connection is over: pending item actions, prunes, reloads and the disk use read are forgotten. With a
    /// <paramref name="lostMessage"/> (the connection was lost) running pulls and an unfinished run fail with it; without one (the session
    /// starts over) they are forgotten and every list counts as not loaded until it is read again.
    /// </summary>
    private static EngineState ForgetUnderWay(EngineState state, string? lostMessage)
    {
        var next = state;
        foreach (var kind in Kinds)
        {
            var status = StatusOf(next, kind);
            next = WithStatus(next, kind, status with
            {
                Loaded = lostMessage is not null && status.Loaded,
                ReloadScheduled = false,
                Pending = status.Pending.Clear(),
                Pruning = false,
            });
        }

        var running = next.Pulls.Values.Where(p => p.Status == PullStatus.Running).ToList();
        var pulls = lostMessage is null
            ? next.Pulls.RemoveRange(running.Select(p => p.Reference))
            : next.Pulls.SetItems(running.Select(p => KeyValuePair.Create(p.Reference, p with { Status = PullStatus.Failed, Error = lostMessage })));
        var run = next.Run is { Status: RunStatus.InProgress } inProgress
            ? lostMessage is null ? null : inProgress with { Status = RunStatus.Failed, Error = lostMessage }
            : next.Run;

        return next with { Pulls = pulls, Run = run, DiskUsage = next.DiskUsage with { Loading = false } };
    }

    /// <summary>Reads every list whose page has been shown, and the disk use once it has been asked for: the connection is new.</summary>
    private static (EngineState, ImmutableArray<SessionEffect>) LoadWanted(EngineState state)
    {
        var result = ReloadWantedLists(state);
        return state.DiskUsage.Asked ? result.Then(LoadDiskUsage) : result;
    }

    /// <summary>Reads every list whose page has been shown, now.</summary>
    private static (EngineState, ImmutableArray<SessionEffect>) ReloadWantedLists(EngineState state)
    {
        var result = (state, None);
        foreach (var kind in Kinds.Where(k => StatusOf(state, k).Wanted))
            result = result.Then(s => Load(s, kind));
        return result;
    }

    /// <summary>
    /// The list reloads due to an engine event: the event's own list, and images and volumes when a container is created or destroyed.
    /// </summary>
    private static (EngineState, ImmutableArray<SessionEffect>) ReloadListsFor(EngineState state, EngineEventMessage message)
    {
        var action = BaseAction(message.Action);
        ResourceKind[] kinds = message.Type switch
        {
            "image" when ImageReloadActions.Contains(action) => [ResourceKind.Images],
            "volume" when VolumeReloadActions.Contains(action) => [ResourceKind.Volumes],
            "network" when NetworkReloadActions.Contains(action) => [ResourceKind.Networks],
            "container" when InUseActions.Contains(action) => [ResourceKind.Images, ResourceKind.Volumes],
            _ => [],
        };

        var result = (state, None);
        foreach (var kind in kinds)
            result = result.Then(s => RequestListReload(s, kind));
        return result;
    }

    private static (EngineState, ImmutableArray<SessionEffect>) Want(EngineState state, ResourceKind kind, bool connected)
    {
        var status = StatusOf(state, kind);
        if (status.Wanted)
            return (state, None);
        var wanted = WithStatus(state, kind, status with { Wanted = true });
        return connected ? Load(wanted, kind) : (wanted, None);
    }

    /// <summary>Reads the list now with a new generation, which supersedes any read still in flight.</summary>
    private static (EngineState, ImmutableArray<SessionEffect>) Load(EngineState state, ResourceKind kind)
    {
        var ticket = state.Ticket + 1;
        return (
            WithStatus(state with { Ticket = ticket }, kind, StatusOf(state, kind) with { Generation = ticket }),
            [new LoadResourceEffect(kind, ticket)]);
    }

    /// <summary>Schedules a debounced reload of a list whose page has been shown, unless one is already waiting.</summary>
    private static (EngineState, ImmutableArray<SessionEffect>) RequestListReload(EngineState state, ResourceKind kind)
    {
        var status = StatusOf(state, kind);
        return !status.Wanted || status.ReloadScheduled
            ? (state, None)
            : (WithStatus(state, kind, status with { ReloadScheduled = true }), [new ScheduleResourceReloadEffect(kind, ReloadDebounce)]);
    }

    /// <summary>The list arrived: it is loaded, and pending keys it no longer holds are forgotten.</summary>
    private static ListStatus Arrived(ListStatus status, IEnumerable<string> keys)
    {
        var listed = keys.ToHashSet(StringComparer.Ordinal);
        return status with { Loaded = true, Pending = status.Pending.RemoveRange(status.Pending.Keys.Where(k => !listed.Contains(k))) };
    }

    private static (EngineState, ImmutableArray<SessionEffect>) RequestItems(
        EngineState state, ResourceKind kind, IEnumerable<string> keys, ItemAction action, bool force)
    {
        var status = StatusOf(state, kind);
        var pending = status.Pending.ToBuilder();
        var effects = ImmutableArray.CreateBuilder<SessionEffect>();
        foreach (var key in keys)
        {
            if (pending.TryAdd(key, action))
                effects.Add(new ItemActionEffect(kind, key, action, force));
        }

        return effects.Count == 0
            ? (state, None)
            : (WithStatus(state, kind, status with { Pending = pending.ToImmutable() }), effects.ToImmutable());
    }

    private static (EngineState, ImmutableArray<SessionEffect>) LoadDiskUsage(EngineState state)
    {
        var ticket = state.Ticket + 1;
        return (
            state with { Ticket = ticket, DiskUsage = state.DiskUsage with { Asked = true, Loading = true, Generation = ticket } },
            [new LoadDiskUsageEffect(ticket)]);
    }

    /// <summary>After a remove or prune: reads the disk use again if something asked for it before; never otherwise.</summary>
    private static (EngineState, ImmutableArray<SessionEffect>) ReloadAskedDiskUsage(EngineState state) =>
        state.DiskUsage.Asked ? LoadDiskUsage(state) : (state, None);

    private static (EngineState, ImmutableArray<SessionEffect>) StartPull(EngineState state, string typed)
    {
        if (ImagePull.KeyOf(typed) is not { } key || state.Pulls.TryGetValue(key, out var current) && current.Status == PullStatus.Running)
            return (state, None);

        var ticket = state.Ticket + 1;
        return (
            state with { Ticket = ticket, Pulls = state.Pulls.SetItem(key, ImagePull.Start(key, ticket)) },
            [new StartPullEffect(key, ticket)]);
    }

    /// <summary>The key of a pull named as typed or as already keyed; a reference that does not parse is its own key.</summary>
    private static string PullKey(string reference) => ImagePull.KeyOf(reference) ?? reference;

    private static bool IsRunning(EngineState state, string reference, int generation, out ImagePull pull) =>
        state.Pulls.TryGetValue(reference, out pull!) && pull.Status == PullStatus.Running && pull.Generation == generation;

    private static ListStatus StatusOf(EngineState state, ResourceKind kind) => kind switch
    {
        ResourceKind.Images => state.Images.Status,
        ResourceKind.Volumes => state.Volumes.Status,
        _ => state.Networks.Status,
    };

    private static EngineState WithStatus(EngineState state, ResourceKind kind, ListStatus status) => kind switch
    {
        ResourceKind.Images => state with { Images = state.Images with { Status = status } },
        ResourceKind.Volumes => state with { Volumes = state.Volumes with { Status = status } },
        _ => state with { Networks = state.Networks with { Status = status } },
    };

    /// <summary>Runs <paramref name="next"/> on this result's state and appends its effects to this result's.</summary>
    private static (EngineState, ImmutableArray<SessionEffect>) Then(
        this (EngineState State, ImmutableArray<SessionEffect> Effects) first,
        Func<EngineState, (EngineState State, ImmutableArray<SessionEffect> Effects)> next)
    {
        var (state, effects) = next(first.State);
        return (state, first.Effects.AddRange(effects));
    }
}

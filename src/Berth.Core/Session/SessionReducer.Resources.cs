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

    /// <summary>How many items of a batch of removes are sent at once.</summary>
    public const int RemovalConcurrency = 4;

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
                var next = state with { Images = new(loaded.Images, Arrived(state.Images.Status)) };
                var shown = next.Pulls.Values.Where(p => p.Status == PullStatus.Done && loaded.Generation > p.FinishedAtLoad).Select(p => p.Reference);
                return (next with { Pulls = next.Pulls.RemoveRange(shown) }, None);
            }

            case VolumesLoaded loaded when connected && loaded.Generation == state.Volumes.Status.Generation:
                return (state with { Volumes = new(loaded.Volumes, Arrived(state.Volumes.Status)) }, None);

            case NetworksLoaded loaded when connected && loaded.Generation == state.Networks.Status.Generation:
                return (state with { Networks = new(loaded.Networks, Arrived(state.Networks.Status)) }, None);

            case ResourceLoadFailed failure when connected && failure.Generation == StatusOf(state, failure.Kind).Generation:
                return failure.EngineUnreachable
                    ? Lose(state, failure.Message)
                    : (WithStatus(state, failure.Kind, StatusOf(state, failure.Kind) with { LoadError = failure.Message }) with { ActionError = failure.Message }, None);

            case ResourceReloadDue due:
            {
                var status = StatusOf(state, due.Kind);
                var cleared = status.ReloadScheduled ? WithStatus(state, due.Kind, status with { ReloadScheduled = false }) : state;
                return connected ? Load(cleared, due.Kind) : (cleared, None);
            }

            case RemoveRequested request when connected:
                return RequestRemoval(state, request.Kind, request.Items);

            case RemoveItemFinished finished when connected && StatusOf(state, finished.Kind).Removal is { Running: > 0 }:
                return FinishRemovalItem(state, finished);

            case RemovalDismissed dismissed when StatusOf(state, dismissed.Kind).Removal is { IsRunning: false }:
                return (WithStatus(state, dismissed.Kind, StatusOf(state, dismissed.Kind) with { Removal = null }), None);

            case CreateVolumeRequested request when connected && !state.Volumes.Pending.ContainsKey(request.Name):
                return (
                    WithStatus(state, ResourceKind.Volumes, state.Volumes.Status with { Pending = state.Volumes.Pending.SetItem(request.Name, ItemAction.Create) }),
                    [new CreateVolumeEffect(request.Name)]);

            case VolumeCreated created when connected:
                return RequestListReload(
                    WithStatus(state, ResourceKind.Volumes, state.Volumes.Status with { Pending = state.Volumes.Pending.Remove(created.Name) }),
                    ResourceKind.Volumes);

            case VolumeCreateFailed failure when connected:
            {
                if (failure.EngineUnreachable)
                    return Lose(state, failure.Message);
                var next = WithStatus(state, ResourceKind.Volumes, state.Volumes.Status with { Pending = state.Volumes.Pending.Remove(failure.Name) });
                return RequestListReload(next with { ActionError = failure.Message }, ResourceKind.Volumes);
            }

            case DiskUsageRequested when connected:
                return LoadDiskUsage(state);

            case DiskUsageLoaded loaded when connected && state.DiskUsage.Loading && loaded.Generation == state.DiskUsage.Generation:
                return ReadEnded(state with { DiskUsage = state.DiskUsage with { Usage = loaded.Usage } });

            case DiskUsageFailed failure when connected && state.DiskUsage.Loading && failure.Generation == state.DiskUsage.Generation:
                return failure.EngineUnreachable
                    ? Lose(state, failure.Message)
                    : ReadEnded(state with { ActionError = failure.Message });

            case PullRequested request when connected:
                return StartPull(state, request.Reference);

            case PullProgressArrived progress when IsRunning(state, progress.Reference, progress.Generation, out var pull):
                return (state with { Pulls = state.Pulls.SetItem(pull.Reference, pull.With(progress.Lines)) }, None);

            case PullFinished finished when IsRunning(state, finished.Reference, finished.Generation, out var pull) && !state.Images.Status.Wanted:
                // No image list to wait for: nothing would ever clear the entry.
                return (state with { Pulls = state.Pulls.Remove(pull.Reference) }, None);

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

            case ImageInspectRequested request when connected:
            {
                var ticket = state.Ticket + 1;
                return (state with { Ticket = ticket, ImageInspect = new ImageInspect(request.Id, ticket, null, null) }, [new InspectImageEffect(request.Id, ticket)]);
            }

            case ImageInspected read when connected && state.ImageInspect is { IsLoading: true } inspect && read.Generation == inspect.Generation:
                return (state with { ImageInspect = inspect with { Inspection = read.Inspection } }, None);

            case ImageInspectFailed failure when connected && state.ImageInspect is { IsLoading: true } inspect && failure.Generation == inspect.Generation:
                return failure.EngineUnreachable
                    ? Lose(state, failure.Message)
                    : (state with { ImageInspect = inspect with { Error = failure.Message } }, None);

            case ImageInspectDismissed:
                return (state with { ImageInspect = null }, None);

            case RunContainerRequested request when connected && state.Run?.Status != RunStatus.InProgress:
                return (state with { Run = new ContainerRun(request.Request.Image, RunStatus.InProgress, null, null) }, [new RunContainerEffect(request.Request)]);

            case RunCreated created when connected && state.Run is { Status: RunStatus.InProgress } run:
                return (state with { Run = run with { ContainerId = created.ContainerId } }, None);

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
    /// What is under way on the connection is over, lost or started over: pending item actions, reloads and the disk use read are
    /// forgotten, a batch of removes ends where it is (keeping its refusals), running pulls and an unfinished run are interrupted (a run keeps the ID of a container already created), and an image read is forgotten. When
    /// the session starts over (<paramref name="startingOver"/>) every list also counts as not loaded until it is read again.
    /// </summary>
    private static EngineState ForgetUnderWay(EngineState state, bool startingOver)
    {
        var next = state;
        foreach (var kind in Kinds)
        {
            var status = StatusOf(next, kind);
            next = WithStatus(next, kind, status with
            {
                Loaded = !startingOver && status.Loaded,
                ReloadScheduled = false,
                Pending = status.Pending.Clear(),
                // The batch ends here: what waited is not sent, and what was being sent got no answer.
                Removal = status.Removal is { IsRunning: true } batch
                    ? batch with { Running = 0, Queued = [], Interrupted = true, NotSent = batch.Queued.Length, Unanswered = batch.Running }
                    : status.Removal,
            });
        }

        var pulls = next.Pulls.SetItems(next.Pulls.Values
            .Where(p => p.Status == PullStatus.Running)
            .Select(p => KeyValuePair.Create(p.Reference, p with { Status = PullStatus.Interrupted })));
        var run = next.Run is { Status: RunStatus.InProgress } inProgress ? inProgress with { Status = RunStatus.Interrupted } : next.Run;

        return next with { Pulls = pulls, Run = run, DiskUsage = next.DiskUsage with { Loading = false, Again = false }, ImageInspect = null };
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
        // Shown again after its read failed: read it again.
        if (status.Wanted && (status.LoadError is null || !connected))
            return (state, None);
        var wanted = WithStatus(state, kind, status with { Wanted = true });
        return connected ? Load(wanted, kind) : (wanted, None);
    }

    /// <summary>Reads the list now with a new generation, which supersedes any read still in flight.</summary>
    private static (EngineState, ImmutableArray<SessionEffect>) Load(EngineState state, ResourceKind kind)
    {
        var ticket = state.Ticket + 1;
        return (
            WithStatus(state with { Ticket = ticket }, kind, StatusOf(state, kind) with { Generation = ticket, LoadError = null }),
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
    private static ListStatus Arrived(ListStatus status)
    {
        // Pending keys stay: an item waiting its turn keeps its keys until it is sent or skipped, even when they have left the list.
        return status with { Loaded = true, LoadError = null };
    }

    /// <summary>
    /// Adds <paramref name="items"/> to the list's batch of removes (starting a new batch when none runs), leaving out an item with a key
    /// already pending, and marks every key pending. Then sends as many as <see cref="RemovalConcurrency"/> allows.
    /// </summary>
    private static (EngineState, ImmutableArray<SessionEffect>) RequestRemoval(EngineState state, ResourceKind kind, IEnumerable<RemovalItem> items)
    {
        var status = StatusOf(state, kind);
        var pending = status.Pending.ToBuilder();
        var accepted = ImmutableArray.CreateBuilder<RemovalItem>();
        foreach (var item in items)
        {
            if (item.Keys.IsEmpty || item.Keys.Any(pending.ContainsKey) || item.Keys.Distinct(StringComparer.Ordinal).Count() != item.Keys.Length)
                continue;
            foreach (var key in item.Keys)
                pending[key] = ItemAction.Remove;
            accepted.Add(item);
        }

        if (accepted.Count == 0)
            return (state, None);

        var batch = status.Removal is { IsRunning: true } running ? running : RemovalBatch.Empty;
        batch = batch with { Total = batch.Total + accepted.Count, Queued = batch.Queued.AddRange(accepted) };
        return Advance(WithStatus(state, kind, status with { Pending = pending.ToImmutable(), Removal = batch }), kind);
    }

    /// <summary>
    /// An item ended: its keys are no longer pending, a refusal is kept, the next waiting item is sent, and when the batch has ended the
    /// list is read again and, once it has been asked for and the list is one it covers, the disk use once.
    /// </summary>
    private static (EngineState, ImmutableArray<SessionEffect>) FinishRemovalItem(EngineState state, RemoveItemFinished finished)
    {
        if (finished.EngineUnreachable)
            return Lose(state, finished.Refusal?.Message ?? "");

        var status = StatusOf(state, finished.Kind);
        var batch = status.Removal!;
        batch = batch with
        {
            Finished = batch.Finished + 1,
            Running = batch.Running - 1,
            Refusals = finished.Refusal is { } refusal ? batch.Refusals.Add(refusal) : batch.Refusals,
        };
        var next = WithStatus(state, finished.Kind, status with { Pending = status.Pending.RemoveRange(finished.Item.Keys), Removal = batch });
        return Advance(next, finished.Kind);
    }

    /// <summary>
    /// Sends waiting items of the list's batch while fewer than <see cref="RemovalConcurrency"/> are being sent, skipping (and counting as
    /// ended) any that no longer holds (<see cref="SkipReasonOf"/>). When that ends the batch, the list is read again and, once it has
    /// been asked for and the list is one it covers, the disk use once.
    /// </summary>
    private static (EngineState, ImmutableArray<SessionEffect>) Advance(EngineState state, ResourceKind kind)
    {
        var status = StatusOf(state, kind);
        if (status.Removal is not { } batch)
            return (state, None);

        var effects = ImmutableArray.CreateBuilder<SessionEffect>();
        var pending = status.Pending;
        while (batch.IsRunning && batch.Running < RemovalConcurrency && !batch.Queued.IsEmpty)
        {
            var item = batch.Queued[0];
            batch = batch with { Queued = batch.Queued.RemoveAt(0) };
            if (SkipReasonOf(state, kind, item) is { } reason)
            {
                batch = batch with { Finished = batch.Finished + 1, Skips = batch.Skips.Add(new RemovalSkip(item.Id, reason)) };
                pending = pending.RemoveRange(item.Keys);
                continue;
            }

            effects.Add(new RemoveItemEffect(kind, item));
            batch = batch with { Running = batch.Running + 1 };
        }

        var next = (WithStatus(state, kind, status with { Pending = pending, Removal = batch }), effects.ToImmutable());
        if (batch.IsRunning)
            return next;

        var reloaded = next.Then(s => RequestListReload(s, kind));
        return kind == ResourceKind.Networks ? reloaded : reloaded.Then(ReloadAskedDiskUsage);
    }

    /// <summary>
    /// Why <paramref name="item"/> must not be sent now, from the lists as they are: it is no longer listed; for an image, a tag among its
    /// keys now names another image (it was built again or moved); for "Remove unused", a container of any state uses it now. Null to send.
    /// </summary>
    private static SkipReason? SkipReasonOf(EngineState state, ResourceKind kind, RemovalItem item)
    {
        switch (kind)
        {
            case ResourceKind.Images:
            {
                var image = state.Images.Items.FirstOrDefault(i => i.Id == item.Id);
                if (image is null)
                    return SkipReason.Gone;
                if (item.Keys.Any(k => k != image.Id && !image.RepoTags.Contains(k)))
                    return SkipReason.TagMoved;
                var used = (image.Containers ?? 0) > 0 || state.Containers.Any(c => c.ImageId == image.Id);
                return item.OnlyIfUnused && used ? SkipReason.InUse : null;
            }

            case ResourceKind.Volumes:
            {
                if (!state.Volumes.Items.Any(v => v.Name == item.Id))
                    return SkipReason.Gone;
                var used = state.Containers.Any(c => c.Mounts.Any(m => m.Type == "volume" && m.Name == item.Id));
                return item.OnlyIfUnused && used ? SkipReason.InUse : null;
            }

            default:
            {
                var network = state.Networks.Items.FirstOrDefault(n => n.Id == item.Id);
                if (network is null)
                    return SkipReason.Gone;
                var used = state.Containers.Any(c => c.Networks.Contains(network.Name));
                return item.OnlyIfUnused && used ? SkipReason.InUse : null;
            }
        }
    }

    /// <summary>Reads the disk use now, or once more after the read that is running (however often it is asked meanwhile).</summary>
    private static (EngineState, ImmutableArray<SessionEffect>) LoadDiskUsage(EngineState state)
    {
        if (state.DiskUsage.Loading)
            return state.DiskUsage.Again ? (state, None) : (state with { DiskUsage = state.DiskUsage with { Again = true } }, None);

        var ticket = state.Ticket + 1;
        return (
            state with { Ticket = ticket, DiskUsage = state.DiskUsage with { Asked = true, Loading = true, Generation = ticket } },
            [new LoadDiskUsageEffect(ticket)]);
    }

    /// <summary>The running disk use read ended: starts the one more read it was asked for meanwhile, if any.</summary>
    private static (EngineState, ImmutableArray<SessionEffect>) ReadEnded(EngineState state)
    {
        var again = state.DiskUsage.Again;
        var ended = state with { DiskUsage = state.DiskUsage with { Loading = false, Again = false } };
        return again ? LoadDiskUsage(ended) : (ended, None);
    }

    /// <summary>After a batch of removes: reads the disk use again if something asked for it before; never otherwise.</summary>
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

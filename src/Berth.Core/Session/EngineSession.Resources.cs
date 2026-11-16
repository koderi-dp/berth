using Berth.Engine;
using Berth.State;

namespace Berth.Session;

/// <summary>The runtime of the images, volumes and networks lists, their actions, disk use and running a container.</summary>
public sealed partial class EngineSession
{
    // What failed, as told to the error sink. Fixed wording: the exception carries the rest.
    private const string ImagesLoadFailedReport = "loading the images failed";
    private const string VolumesLoadFailedReport = "loading the volumes failed";
    private const string NetworksLoadFailedReport = "loading the networks failed";
    private const string ImageActionFailedReport = "an image action failed";
    private const string VolumeActionFailedReport = "a volume action failed";
    private const string NetworkActionFailedReport = "a network action failed";
    private const string DiskUsageFailedReport = "reading the disk use failed";
    private const string RunFailedReport = "running a container failed";

    // Touched only by the loop, and by DisposeAsync once the loop has ended.
    private readonly Dictionary<ResourceKind, ITimer> _listReloadTimers = [];
    private PullRunner? _pulls;

    /// <summary>Starts an effect of the images, volumes and networks lists; ignores any other.</summary>
    private void StartResourceEffect(SessionEffect effect)
    {
        switch (effect)
        {
            case LoadResourceEffect load when _connection is { } connection:
                connection.Run(ct => LoadResourceAsync(connection, load, ct));
                break;

            case ScheduleResourceReloadEffect reload:
                if (_listReloadTimers.Remove(reload.Kind, out var timer))
                    timer.Dispose();
                _listReloadTimers[reload.Kind] = StartTimer(reload.Delay, new ResourceReloadDue(reload.Kind), _connection);
                break;

            case RemoveItemEffect remove when _connection is { } connection:
                connection.Run(ct => RemoveItemAsync(connection, remove, ct));
                break;

            case CreateVolumeEffect create when _connection is { } connection:
                connection.Run(ct => CallAsync(
                    connection,
                    VolumeActionFailedReport,
                    async (api, token) =>
                    {
                        await api.CreateVolumeAsync(create.Name, token).ConfigureAwait(false);
                        return new VolumeCreated(create.Name);
                    },
                    ex => new VolumeCreateFailed(create.Name, ex.Message, IsUnreachable(ex)),
                    ct));
                break;

            case LoadDiskUsageEffect load when _connection is { } connection:
                connection.Run(ct => CallAsync(
                    connection,
                    DiskUsageFailedReport,
                    async (api, token) => new DiskUsageLoaded(load.Generation, await api.GetDiskUsageAsync(token).ConfigureAwait(false)),
                    ex => new DiskUsageFailed(load.Generation, ex.Message, IsUnreachable(ex)),
                    ct));
                break;

            case StartPullEffect pull when _connection is { } connection:
                _pulls ??= new PullRunner(this, connection);
                _pulls.Start(pull.Reference, pull.Generation);
                break;

            case CancelPullEffect cancel:
                _pulls?.Cancel(cancel.Reference);
                break;

            case RunContainerEffect run when _connection is { } connection:
                connection.Run(ct => RunContainerAsync(connection, run, ct));
                break;
        }
    }

    /// <summary>Stops the list reload timers and the pulls of the connection being retired.</summary>
    private void RetireResources()
    {
        foreach (var timer in _listReloadTimers.Values)
            timer.Dispose();
        _listReloadTimers.Clear();
        _pulls?.Dispose();
        _pulls = null;
    }

    private Task LoadResourceAsync(Connection connection, LoadResourceEffect load, CancellationToken ct) => load.Kind switch
    {
        ResourceKind.Images => CallAsync(
            connection,
            ImagesLoadFailedReport,
            async (api, token) => new ImagesLoaded(load.Generation, [.. await api.ListImagesAsync(token).ConfigureAwait(false)]),
            ex => new ResourceLoadFailed(load.Kind, load.Generation, ex.Message, IsUnreachable(ex)),
            ct),
        ResourceKind.Volumes => CallAsync(
            connection,
            VolumesLoadFailedReport,
            async (api, token) => new VolumesLoaded(load.Generation, [.. await api.ListVolumesAsync(token).ConfigureAwait(false)]),
            ex => new ResourceLoadFailed(load.Kind, load.Generation, ex.Message, IsUnreachable(ex)),
            ct),
        _ => CallAsync(
            connection,
            NetworksLoadFailedReport,
            async (api, token) => new NetworksLoaded(load.Generation, [.. await api.ListNetworksAsync(token).ConfigureAwait(false)]),
            ex => new ResourceLoadFailed(load.Kind, load.Generation, ex.Message, IsUnreachable(ex)),
            ct),
    };

    /// <summary>Removes the item's keys one after another, never forced, and stops at the first the engine refuses.</summary>
    private async Task RemoveItemAsync(Connection connection, RemoveItemEffect remove, CancellationToken ct)
    {
        var report = remove.Kind switch
        {
            ResourceKind.Images => ImageActionFailedReport,
            ResourceKind.Volumes => VolumeActionFailedReport,
            _ => NetworkActionFailedReport,
        };
        var api = connection.Api!;
        RemovalRefusal? refusal = null;
        var unreachable = false;
        foreach (var key in remove.Item.Keys)
        {
            try
            {
                await (remove.Kind switch
                {
                    ResourceKind.Images => api.RemoveImageAsync(key, force: false, ct),
                    ResourceKind.Volumes => api.RemoveVolumeAsync(key, force: false, ct),
                    _ => api.RemoveNetworkAsync(key, ct),
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Report(report, ex);
                refusal = new RemovalRefusal(key, ex.Message);
                unreachable = IsUnreachable(ex);
                break;
            }
        }

        Post(new RemoveItemFinished(remove.Kind, remove.Item, refusal, unreachable), connection);
    }

    /// <summary>Creates the container, then starts it. A start that fails says which container was created.</summary>
    private async Task RunContainerAsync(Connection connection, RunContainerEffect run, CancellationToken ct)
    {
        string? created = null;
        SessionEvent result;
        try
        {
            var api = connection.Api!;
            created = await api.CreateContainerAsync(run.Request, ct).ConfigureAwait(false);
            Post(new RunCreated(created), connection);
            await api.StartContainerAsync(created, ct).ConfigureAwait(false);
            result = new RunSucceeded(created);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Report(RunFailedReport, ex);
            result = new RunFailed(created, ex.Message, IsUnreachable(ex));
        }

        Post(result, connection);
    }

    /// <summary>
    /// Makes one engine call on <paramref name="connection"/> and posts what it answers, or reports the failure and posts what
    /// <paramref name="failed"/> makes of it. A cancellation the session caused posts nothing.
    /// </summary>
    private async Task CallAsync(
        Connection connection,
        string report,
        Func<IEngineApi, CancellationToken, Task<SessionEvent>> call,
        Func<Exception, SessionEvent> failed,
        CancellationToken ct)
    {
        SessionEvent result;
        try
        {
            result = await call(connection.Api!, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Report(report, ex);
            result = failed(ex);
        }

        Post(result, connection);
    }
}

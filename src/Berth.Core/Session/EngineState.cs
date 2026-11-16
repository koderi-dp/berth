using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.State;

namespace Berth.Session;

public enum ConnectionStatus { Disconnected, Connecting, Connected }

public enum ContainerAction { Start, Stop, Restart, Pause, Unpause, Remove }

/// <summary>
/// Everything the UI shows about one engine connection. Only <see cref="SessionReducer"/> produces new values.
/// </summary>
/// <param name="Pending">Container id to the action in progress on it.</param>
/// <param name="ConnectionError">The last connect or connection failure, as the engine or transport reported it.</param>
/// <param name="ActionError">The last failed action or list load, as the engine reported it, until dismissed.</param>
/// <param name="RetryAttempt">Retries scheduled since the last successful connect; picks the next back-off delay.</param>
/// <param name="LoadGeneration">The generation of the latest container load; results from older loads are ignored.</param>
/// <param name="ReloadScheduled">A debounced reload is waiting for its <see cref="ReloadDue"/>.</param>
/// <param name="ListLoaded">
/// The first list of the current connection has arrived. Cleared on every connect; until it is set, an empty
/// <see cref="Containers"/> means "not loaded yet", not "no containers".
/// </param>
/// <param name="Stats">The latest stats sample of each running container, while <see cref="StatsWanted"/> and connected.</param>
/// <param name="StatsWanted">A screen that shows list stats is open, so the running containers' stats are followed.</param>
/// <param name="Images">The image list, read once its page is shown (<see cref="ImagesWanted"/>).</param>
/// <param name="Volumes">The volume list, read once its page is shown (<see cref="VolumesWanted"/>).</param>
/// <param name="Networks">The network list, read once its page is shown (<see cref="NetworksWanted"/>).</param>
/// <param name="DiskUsage">The engine's disk use of images and volumes, read when asked for (<see cref="DiskUsageRequested"/>).</param>
/// <param name="Pulls">The image pulls the user started, by the reference they run under (<see cref="ImagePull.KeyOf"/>).</param>
/// <param name="Run">The container the run dialog asked for, until dismissed; null when none.</param>
/// <param name="ContainersLoadError">
/// Why the last read of the container list failed, as the engine said it; null once a read starts or succeeds. With
/// <see cref="ListLoaded"/> false it tells the page the list failed rather than is loading; F5 reads it again.
/// </param>
/// <param name="Ticket">The last generation handed to an image, volume, network or disk use read or a pull.</param>
public sealed record EngineState(
    ConnectionStatus Status,
    EngineVersion? Version,
    EngineInfo? Info,
    ImmutableArray<ContainerSummary> Containers,
    ImmutableDictionary<string, ContainerAction> Pending,
    string? ConnectionError,
    string? ActionError,
    int RetryAttempt,
    int LoadGeneration,
    bool ReloadScheduled,
    bool ListLoaded,
    ImmutableDictionary<string, StatsSample> Stats,
    bool StatsWanted,
    ResourceList<ImageSummary> Images,
    ResourceList<VolumeSummary> Volumes,
    ResourceList<NetworkSummary> Networks,
    DiskUsageState DiskUsage,
    ImmutableDictionary<string, ImagePull> Pulls,
    ContainerRun? Run,
    int Ticket,
    string? ContainersLoadError = null)
{
    public static readonly EngineState Initial = new(
        ConnectionStatus.Disconnected,
        Version: null,
        Info: null,
        Containers: [],
        Pending: ImmutableDictionary<string, ContainerAction>.Empty,
        ConnectionError: null,
        ActionError: null,
        RetryAttempt: 0,
        LoadGeneration: 0,
        ReloadScheduled: false,
        ListLoaded: false,
        Stats: ImmutableDictionary<string, StatsSample>.Empty.WithComparers(StringComparer.Ordinal),
        StatsWanted: false,
        Images: ResourceList<ImageSummary>.Initial,
        Volumes: ResourceList<VolumeSummary>.Initial,
        Networks: ResourceList<NetworkSummary>.Initial,
        DiskUsage: DiskUsageState.Initial,
        Pulls: ImmutableDictionary<string, ImagePull>.Empty.WithComparers(StringComparer.Ordinal),
        Run: null,
        Ticket: 0);
}

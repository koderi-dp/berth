using Berth.Engine.Models;
using Berth.State;

namespace Berth.Session;

/// <summary>List images, volumes or networks; answers with ImagesLoaded, VolumesLoaded or NetworksLoaded, or ResourceLoadFailed, carrying the same generation.</summary>
public sealed record LoadResourceEffect(ResourceKind Kind, int Generation) : SessionEffect;

/// <summary>Dispatch ResourceReloadDue for <see cref="Kind"/> after <see cref="Delay"/>.</summary>
public sealed record ScheduleResourceReloadEffect(ResourceKind Kind, TimeSpan Delay) : SessionEffect;

/// <summary>
/// Remove <see cref="Item"/>'s keys one after another, never forced, stopping at the first refusal; answers with RemoveItemFinished.
/// </summary>
public sealed record RemoveItemEffect(ResourceKind Kind, RemovalItem Item) : SessionEffect;

/// <summary>Create the volume <see cref="Name"/>; answers with VolumeCreated or VolumeCreateFailed.</summary>
public sealed record CreateVolumeEffect(string Name) : SessionEffect;

/// <summary>Read the disk use; answers with DiskUsageLoaded or DiskUsageFailed carrying the same generation.</summary>
public sealed record LoadDiskUsageEffect(int Generation) : SessionEffect;

/// <summary>
/// Pull <see cref="Reference"/>, sending its progress as PullProgressArrived at most every <see cref="EngineSession.PullBatchInterval"/>,
/// then PullFinished or PullFailed, all carrying <see cref="Generation"/>.
/// </summary>
public sealed record StartPullEffect(string Reference, int Generation) : SessionEffect;

/// <summary>Stop the pull of <see cref="Reference"/>; it sends nothing more.</summary>
public sealed record CancelPullEffect(string Reference) : SessionEffect;

/// <summary>Create a container, then start it; answers with RunSucceeded or RunFailed.</summary>
public sealed record RunContainerEffect(ContainerCreateRequest Request) : SessionEffect;

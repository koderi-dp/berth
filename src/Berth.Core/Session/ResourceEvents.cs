using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.State;

namespace Berth.Session;

/// <summary>The Images page was shown: read the image list if it has not been read yet, and keep it fresh from then on.</summary>
public sealed record ImagesWanted : SessionEvent;

/// <summary>The Volumes page was shown: read the volume list if it has not been read yet, and keep it fresh from then on.</summary>
public sealed record VolumesWanted : SessionEvent;

/// <summary>The Networks page was shown: read the network list if it has not been read yet, and keep it fresh from then on.</summary>
public sealed record NetworksWanted : SessionEvent;

public sealed record ImagesLoaded(int Generation, ImmutableArray<ImageSummary> Images) : SessionEvent;

public sealed record VolumesLoaded(int Generation, ImmutableArray<VolumeSummary> Volumes) : SessionEvent;

public sealed record NetworksLoaded(int Generation, ImmutableArray<NetworkSummary> Networks) : SessionEvent;

/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record ResourceLoadFailed(ResourceKind Kind, int Generation, string Message, bool EngineUnreachable) : SessionEvent;

/// <summary>A debounced reload of one list is due.</summary>
public sealed record ResourceReloadDue(ResourceKind Kind) : SessionEvent;

/// <summary>
/// Remove <paramref name="Items"/> of one list, a few at a time, each item's keys in order (a confirmed delete or "Remove unused …",
/// as Presentation's plans make them; never forced). An item with a key already pending is left out. Joins a batch that is running.
/// </summary>
public sealed record RemoveRequested(ResourceKind Kind, ImmutableArray<RemovalItem> Items) : SessionEvent;

/// <summary>An item of a remove ended: every key removed, or stopped at the key the engine refused (<paramref name="Refusal"/>).</summary>
/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record RemoveItemFinished(ResourceKind Kind, RemovalItem Item, RemovalRefusal? Refusal, bool EngineUnreachable = false) : SessionEvent;

/// <summary>Forget the ended batch of removes of one list and its refusals.</summary>
public sealed record RemovalDismissed(ResourceKind Kind) : SessionEvent;

/// <summary>Create a volume named <paramref name="Name"/>.</summary>
public sealed record CreateVolumeRequested(string Name) : SessionEvent;

public sealed record VolumeCreated(string Name) : SessionEvent;

/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record VolumeCreateFailed(string Name, string Message, bool EngineUnreachable) : SessionEvent;

/// <summary>Read the engine's disk use of images and volumes now (the page asked, or the user refreshed it).</summary>
public sealed record DiskUsageRequested : SessionEvent;

public sealed record DiskUsageLoaded(int Generation, DiskUsage Usage) : SessionEvent;

/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record DiskUsageFailed(int Generation, string Message, bool EngineUnreachable) : SessionEvent;

/// <summary>
/// Pull <paramref name="Reference"/> as the user typed it ("nginx" pulls nginx:latest). Ignored when it is no reference or a pull of it
/// is already running.
/// </summary>
public sealed record PullRequested(string Reference) : SessionEvent;

/// <summary>Stop the pull of <paramref name="Reference"/> and forget it.</summary>
public sealed record PullCancelRequested(string Reference) : SessionEvent;

/// <summary>Forget the failed pull of <paramref name="Reference"/>.</summary>
public sealed record PullDismissed(string Reference) : SessionEvent;

/// <summary>The progress lines of one pull read since its last batch, in order.</summary>
public sealed record PullProgressArrived(string Reference, int Generation, ImmutableArray<PullProgress> Lines) : SessionEvent;

public sealed record PullFinished(string Reference, int Generation) : SessionEvent;

/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record PullFailed(string Reference, int Generation, string Message, bool EngineUnreachable) : SessionEvent;

/// <summary>Create a container from <paramref name="Request"/> and start it. Ignored while another run is in progress.</summary>
public sealed record RunContainerRequested(ContainerCreateRequest Request) : SessionEvent;

/// <summary>The container was created; starting it comes next.</summary>
public sealed record RunCreated(string ContainerId) : SessionEvent;

/// <summary>The container was created and started.</summary>
public sealed record RunSucceeded(string ContainerId) : SessionEvent;

/// <param name="CreatedId">The new container's ID when the create went through and the start failed; null when the create failed.</param>
/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record RunFailed(string? CreatedId, string Message, bool EngineUnreachable) : SessionEvent;

/// <summary>The run dialog is done with the result (closed, or the app opened the new container). Ignored while the run is in progress.</summary>
public sealed record RunDismissed : SessionEvent;

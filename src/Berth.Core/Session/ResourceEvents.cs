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
/// Remove each of <paramref name="References"/>: an image ID removes the image, a tag ("nginx:latest") removes that tag (and the image
/// with it when it was the last). <paramref name="Force"/> is the engine's force flag.
/// </summary>
public sealed record RemoveImagesRequested(ImmutableArray<string> References, bool Force = false) : SessionEvent;

public sealed record RemoveVolumesRequested(ImmutableArray<string> Names) : SessionEvent;

public sealed record RemoveNetworksRequested(ImmutableArray<string> Ids) : SessionEvent;

/// <summary>
/// Remove what no container uses, of one kind: every unused image (or with <paramref name="DanglingImagesOnly"/> only the untagged
/// ones), every unused volume (named or anonymous), or every unused network.
/// </summary>
public sealed record PruneRequested(ResourceKind Kind, bool DanglingImagesOnly = false) : SessionEvent;

/// <summary>Create a volume named <paramref name="Name"/>.</summary>
public sealed record CreateVolumeRequested(string Name) : SessionEvent;

/// <summary>A remove of <paramref name="Key"/>, or the create of a volume of that name, went through.</summary>
public sealed record ItemActionSucceeded(ResourceKind Kind, string Key) : SessionEvent;

/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record ItemActionFailed(ResourceKind Kind, string Key, string Message, bool EngineUnreachable) : SessionEvent;

public sealed record PruneSucceeded(ResourceKind Kind, PruneResult Result) : SessionEvent;

/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record PruneFailed(ResourceKind Kind, string Message, bool EngineUnreachable) : SessionEvent;

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

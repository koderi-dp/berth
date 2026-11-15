using System.Collections.Immutable;
using Berth.Engine.Models;

namespace Berth.Session;

/// <summary>Something that happened: a user request, an engine call's result, an engine event or a timer.</summary>
public abstract record SessionEvent;

public sealed record ConnectRequested : SessionEvent;

public sealed record ConnectSucceeded(EngineVersion Version, EngineInfo Info) : SessionEvent;

public sealed record ConnectFailed(string Message) : SessionEvent;

public sealed record ConnectionLost(string Message) : SessionEvent;

public sealed record RetryDue : SessionEvent;

public sealed record ContainersLoaded(int Generation, ImmutableArray<ContainerSummary> Containers, EngineInfo Info) : SessionEvent;

/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record LoadFailed(int Generation, string Message, bool EngineUnreachable) : SessionEvent;

public sealed record EngineEventReceived(EngineEventMessage Message) : SessionEvent;

public sealed record ReloadDue : SessionEvent;

/// <summary>The user asked for the list to be read again now (F5).</summary>
public sealed record ReloadRequested : SessionEvent;

/// <param name="Force">Applies to <see cref="ContainerAction.Remove"/>.</param>
public sealed record ActionRequested(ImmutableArray<string> Ids, ContainerAction Action, bool Force = false) : SessionEvent;

public sealed record ActionSucceeded(string Id) : SessionEvent;

/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record ActionFailed(string Id, string Message, bool EngineUnreachable) : SessionEvent;

public sealed record ActionErrorDismissed : SessionEvent;

/// <summary>A screen that shows list stats opened (<paramref name="Wanted"/>) or closed.</summary>
public sealed record StatsWantedChanged(bool Wanted) : SessionEvent;

/// <summary>The latest stats sample of each watched container that sent one since the last batch.</summary>
public sealed record StatsBatchArrived(ImmutableDictionary<string, StatsSample> Latest) : SessionEvent;

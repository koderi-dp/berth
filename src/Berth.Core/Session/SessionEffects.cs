namespace Berth.Session;

/// <summary>Work the reducer asks the session runtime to carry out.</summary>
public abstract record SessionEffect;

/// <summary>Negotiate the API version and read <c>/info</c>; answers with ConnectSucceeded or ConnectFailed.</summary>
public sealed record ConnectEffect : SessionEffect;

/// <summary>List containers and read <c>/info</c>; answers with ContainersLoaded or LoadFailed carrying the same generation.</summary>
public sealed record LoadContainersEffect(int Generation) : SessionEffect;

/// <summary>Follow <c>/events</c>, dispatching EngineEventReceived, and ConnectionLost when the stream ends.</summary>
public sealed record StartEventStreamEffect : SessionEffect;

/// <summary>Stop every stream started for the current connection.</summary>
public sealed record StopStreamsEffect : SessionEffect;

/// <summary>Dispatch RetryDue after <see cref="Delay"/>.</summary>
public sealed record ScheduleRetryEffect(TimeSpan Delay) : SessionEffect;

/// <summary>Dispatch ReloadDue after <see cref="Delay"/>.</summary>
public sealed record ScheduleReloadEffect(TimeSpan Delay) : SessionEffect;

/// <summary>Run one container action; answers with ActionSucceeded or ActionFailed.</summary>
public sealed record RunActionEffect(string Id, ContainerAction Action, bool Force) : SessionEffect;

using System.Collections.Immutable;
using Berth.Engine.Models;

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
    bool StatsWanted)
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
        StatsWanted: false);
}

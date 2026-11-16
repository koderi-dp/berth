using System.Collections.Immutable;
using Berth.Engine.Models;

namespace Berth.State;

/// <summary>The engine lists other than containers, each with its own page.</summary>
public enum ResourceKind { Images, Volumes, Networks }

/// <summary>An action in progress on one image, volume or network.</summary>
public enum ItemAction { Remove, Create }

/// <summary>
/// Where one of the image, volume and network lists is, apart from its items.
/// </summary>
/// <param name="Wanted">Its page has been shown in this session, so the list is read and kept fresh from engine events.</param>
/// <param name="Loaded">
/// The first list of the current connection has arrived. Cleared on every connect; until it is set, an empty list means "not loaded
/// yet", not "none".
/// </param>
/// <param name="Generation">The latest load; results of older loads are dropped.</param>
/// <param name="ReloadScheduled">A debounced reload is waiting.</param>
/// <param name="Pending">
/// What is under way, by the key it was sent with: an image's ID or one of its tags ("nginx:latest"), a volume's name, a network's ID.
/// A volume being created is pending under its name before it is listed.
/// </param>
/// <param name="Pruning">A prune of this kind is running.</param>
/// <param name="Pruned">What the last prune of this kind removed, until the next one starts.</param>
public sealed record ListStatus(
    bool Wanted,
    bool Loaded,
    int Generation,
    bool ReloadScheduled,
    ImmutableDictionary<string, ItemAction> Pending,
    bool Pruning,
    PruneResult? Pruned)
{
    public static readonly ListStatus Initial = new(
        Wanted: false,
        Loaded: false,
        Generation: 0,
        ReloadScheduled: false,
        Pending: ImmutableDictionary<string, ItemAction>.Empty.WithComparers(StringComparer.Ordinal),
        Pruning: false,
        Pruned: null);
}

/// <summary>The image, volume or network list of one engine session: what the engine listed last (kept while disconnected) and where the list is.</summary>
public sealed record ResourceList<T>(ImmutableArray<T> Items, ListStatus Status)
{
    public static readonly ResourceList<T> Initial = new([], ListStatus.Initial);

    /// <inheritdoc cref="ListStatus.Loaded"/>
    public bool Loaded => Status.Loaded;

    /// <inheritdoc cref="ListStatus.Pending"/>
    public ImmutableDictionary<string, ItemAction> Pending => Status.Pending;

    /// <inheritdoc cref="ListStatus.Pruning"/>
    public bool Pruning => Status.Pruning;
}

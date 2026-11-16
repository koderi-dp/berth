using System.Collections.Immutable;

namespace Berth.State;

/// <summary>The engine lists other than containers, each with its own page.</summary>
public enum ResourceKind { Images, Volumes, Networks }

/// <summary>An action in progress on one image, volume or network.</summary>
public enum ItemAction { Remove, Create }

/// <summary>
/// One thing a remove sends: its keys, removed one after another and stopped at the first refusal. A volume or network is one key (its
/// name or ID); an image is its ID, or the tags being removed from it in order, so the last one deletes the image without forcing.
/// </summary>
public sealed record RemovalItem(ImmutableArray<string> Keys)
{
    public RemovalItem(params string[] keys)
        : this(ImmutableArray.Create(keys))
    {
    }

    /// <summary>Equal when the keys are the same, in the same order (an <see cref="ImmutableArray{T}"/> compares by reference).</summary>
    public bool Equals(RemovalItem? other) => other is not null && Keys.SequenceEqual(other.Keys, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in Keys)
            hash.Add(key, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>A key the engine refused to remove, with its message as it is.</summary>
public sealed record RemovalRefusal(string Key, string Message);

/// <summary>
/// The removes of one list, sent a few at a time: a delete of checked rows, or "Remove unused …". A remove asked for while one runs
/// joins it. It stays, with its refusals, after it ends until it is dismissed or the next one starts.
/// </summary>
/// <param name="Total">Items asked for in this batch.</param>
/// <param name="Finished">Items ended, removed or refused.</param>
/// <param name="Running">Items being sent now.</param>
/// <param name="Queued">Items waiting their turn.</param>
/// <param name="Refusals">Each refusal so far, in the order they came.</param>
public sealed record RemovalBatch(int Total, int Finished, int Running, ImmutableArray<RemovalItem> Queued, ImmutableArray<RemovalRefusal> Refusals)
{
    public static readonly RemovalBatch Empty = new(0, 0, 0, [], []);

    /// <summary>Items are still being sent or waiting.</summary>
    public bool IsRunning => Finished < Total;
}

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
/// What is under way, by key: an image's ID or one of its tags ("nginx:latest"), a volume's name, a network's ID. Every key of a remove
/// that is running or waiting is pending; a volume being created is pending under its name before it is listed.
/// </param>
/// <param name="Removal">The last batch of removes of this list, running or ended; null when none or dismissed.</param>
public sealed record ListStatus(
    bool Wanted,
    bool Loaded,
    int Generation,
    bool ReloadScheduled,
    ImmutableDictionary<string, ItemAction> Pending,
    RemovalBatch? Removal)
{
    public static readonly ListStatus Initial = new(
        Wanted: false,
        Loaded: false,
        Generation: 0,
        ReloadScheduled: false,
        Pending: ImmutableDictionary<string, ItemAction>.Empty.WithComparers(StringComparer.Ordinal),
        Removal: null);
}

/// <summary>The image, volume or network list of one engine session: what the engine listed last (kept while disconnected) and where the list is.</summary>
public sealed record ResourceList<T>(ImmutableArray<T> Items, ListStatus Status)
{
    public static readonly ResourceList<T> Initial = new([], ListStatus.Initial);

    /// <inheritdoc cref="ListStatus.Loaded"/>
    public bool Loaded => Status.Loaded;

    /// <inheritdoc cref="ListStatus.Pending"/>
    public ImmutableDictionary<string, ItemAction> Pending => Status.Pending;

    /// <inheritdoc cref="ListStatus.Removal"/>
    public RemovalBatch? Removal => Status.Removal;
}

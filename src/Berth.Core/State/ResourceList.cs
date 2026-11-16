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
/// <param name="Id">
/// What the item was confirmed for: the image ID (whatever its keys are), the volume name or the network ID. Before the item is sent the
/// session checks that it is still listed and, for an image, that every tag among its keys still names this image.
/// </param>
/// <param name="OnlyIfUnused">"Remove unused …": the item is also skipped if a container uses it by the time it is sent.</param>
/// <param name="Tags">
/// For an image, every tag it had when the confirmation was made (none for an untagged one): the item is skipped if the image's tags are
/// not exactly these by the time it is sent, since the confirmation then said something else would happen. Null for a volume or network.
/// </param>
public sealed record RemovalItem(string Id, ImmutableArray<string> Keys, bool OnlyIfUnused = false, ImmutableArray<string>? Tags = null)
{
    /// <summary>An item whose only key is its <paramref name="id"/>: a volume, a network, or an image removed by ID.</summary>
    public RemovalItem(string id)
        : this(id, [id])
    {
    }

    /// <summary>Equal when the ID, the flag, the keys (in the same order) and the tags seen (in any order) are the same.</summary>
    public bool Equals(RemovalItem? other) =>
        other is not null && Id == other.Id && OnlyIfUnused == other.OnlyIfUnused && Keys.SequenceEqual(other.Keys, StringComparer.Ordinal)
        && (Tags is { } tags ? other.Tags is { } otherTags && tags.ToHashSet(StringComparer.Ordinal).SetEquals(otherTags) : other.Tags is null);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(OnlyIfUnused);
        foreach (var key in Keys)
            hash.Add(key, StringComparer.Ordinal);
        hash.Add(Tags?.Length ?? -1);
        return hash.ToHashCode();
    }
}

/// <summary>A key the engine refused to remove, with its message as it is.</summary>
public sealed record RemovalRefusal(string Key, string Message);

/// <summary>
/// Why an item was not sent: it is no longer listed; an image's tags are not the ones its confirmation saw (one gained, lost or moved to
/// another image); or a container uses it now.
/// </summary>
public enum SkipReason { Gone, TagsChanged, InUse }

/// <summary>An item the session did not send when its turn came, by its <see cref="RemovalItem.Id"/>.</summary>
public sealed record RemovalSkip(string Id, SkipReason Reason);

/// <summary>
/// The removes of one list, sent a few at a time: a delete of checked rows, or "Remove unused …". A remove asked for while one runs
/// joins it. It stays, with its refusals, after it ends until it is dismissed or the next one starts.
/// </summary>
/// <param name="Total">Items asked for in this batch.</param>
/// <param name="Finished">Items ended: removed, refused or skipped.</param>
/// <param name="Running">Items being sent now.</param>
/// <param name="Queued">Items waiting their turn.</param>
/// <param name="Refusals">Each refusal so far, in the order they came.</param>
public sealed record RemovalBatch(int Total, int Finished, int Running, ImmutableArray<RemovalItem> Queued, ImmutableArray<RemovalRefusal> Refusals)
{
    public static readonly RemovalBatch Empty = new(0, 0, 0, [], []);

    /// <summary>Items not sent when their turn came, with why, in order.</summary>
    public ImmutableArray<RemovalSkip> Skips { get; init; } = [];

    /// <summary>The connection changed while the batch ran; it ended there (see <see cref="NotSent"/> and <see cref="Unanswered"/>).</summary>
    public bool Interrupted { get; init; }

    /// <summary>Items still waiting when the batch was interrupted: never sent.</summary>
    public int NotSent { get; init; }

    /// <summary>Items being sent when the batch was interrupted: whether the engine removed them is not known.</summary>
    public int Unanswered { get; init; }

    /// <summary>Items removed: ended without a refusal or a skip.</summary>
    public int Removed => Finished - Refusals.Length - Skips.Length;

    /// <summary>Items are still being sent or waiting.</summary>
    public bool IsRunning => !Interrupted && Finished < Total;
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
/// <param name="LoadError">
/// Why the last read of the list failed, as the engine said it; null once a read succeeds (and on a new connection). With <see cref="Loaded"/> false it
/// tells a page the list failed rather than is loading; showing the page again (its "wanted" event) or F5 reads it again.
/// </param>
public sealed record ListStatus(
    bool Wanted,
    bool Loaded,
    int Generation,
    bool ReloadScheduled,
    ImmutableDictionary<string, ItemAction> Pending,
    RemovalBatch? Removal,
    string? LoadError = null)
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

    /// <inheritdoc cref="ListStatus.LoadError"/>
    public string? LoadError => Status.LoadError;
}

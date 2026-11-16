using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Session;
using Berth.State;

namespace Berth.Presentation;

/// <summary>One thing "Remove unused …" removes: its image ID, volume name or network ID, what the dialog calls it, and what is sent.</summary>
public sealed record PlannedRemoval(string Id, string Name, RemovalItem Item);

/// <summary>
/// A confirmed delete: exactly what is sent (<see cref="Items"/>, each item's keys in order) and the dialog that names it. Build it when
/// the dialog opens (for its text) and again when the user confirms (for what is sent), each time from the rows shown then.
/// </summary>
public sealed record RemovalPlan(ResourceKind Kind, ImmutableArray<RemovalItem> Items, string Title, string Text)
{
    /// <summary>Every key sent, in order.</summary>
    public ImmutableArray<string> Keys => [.. Items.SelectMany(i => i.Keys)];

    /// <summary>Nothing is left to send (the rows went away, are hidden or pending, or the engine is not connected).</summary>
    public bool IsEmpty => Items.IsEmpty;

    /// <summary>The event that sends <see cref="Items"/>.</summary>
    public SessionEvent ToEvent() => new RemoveRequested(Kind, Items);
}

/// <summary>
/// "Remove unused …": what no container uses now, named one by one, the keys each sends (never the engine's prune, which removes whatever
/// is unused when it runs), and the space freed when the disk use tells it. At confirm, <see cref="Confirmations.ConfirmPrune"/> keeps only
/// what the dialog named and is still listed, unused and not pending.
/// </summary>
public sealed record PrunePlan(ResourceKind Kind, bool DanglingImagesOnly, ImmutableArray<PlannedRemoval> Items, long? SpaceFreed, string Title, string Text)
{
    /// <summary>What the dialog names, in order.</summary>
    public IReadOnlyList<string> Names => [.. Items.Select(i => i.Name)];

    /// <summary>There is nothing to remove; the dialog's text says so and its confirm button stays disabled.</summary>
    public bool IsEmpty => Items.IsEmpty;

    /// <summary>The event that removes the items, one by one.</summary>
    public SessionEvent ToEvent() => new RemoveRequested(Kind, [.. Items.Select(i => i.Item)]);
}

/// <summary>
/// What a delete or "Remove unused …" of images, volumes or networks sends, and the confirmation that names it: a delete of checked rows
/// names at most <see cref="RowSelection.NamedInText"/> items, then "and N more"; "Remove unused …" names every item, since nothing else on
/// screen does. Both say the space freed when it is known, and a delete says which items a container uses (the engine refuses those).
/// Nothing is ever forced: the engine's refusal of something in use is shown as it is.
/// </summary>
public static class Confirmations
{
    private const string VolumeDataLostOne = "The data in it is lost for good.";
    private const string VolumeDataLostMany = "The data in them is lost for good.";

    /// <summary>
    /// Deleting the rows <paramref name="keys"/> among <paramref name="shown"/> (the rows the filter and search show now; hidden, gone and
    /// pending ones are left out), grouped by image. A tag of an image that keeps other tags (read from <paramref name="all"/>, the whole
    /// list) removes the tag; an image's only tag, or an untagged image, removes the image by ID; several tags of one image are one item,
    /// removed in list order, so the last of all its tags deletes the image without forcing. <paramref name="usage"/>, when read, tells the
    /// space the deleted images free.
    /// </summary>
    public static RemovalPlan RemoveImages(IReadOnlyList<ImageRow> shown, IReadOnlyList<ImageRow> all, IEnumerable<string> keys, bool connected, DiskUsage? usage)
    {
        var confirmed = RowSelection.Confirmed(shown, keys, connected);
        var chosen = confirmed.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var items = ImmutableArray.CreateBuilder<RemovalItem>();
        var deleted = new List<string>();
        var deletedInUse = new List<ImageRow>();
        var untagOnly = false;
        foreach (var imageId in confirmed.Select(r => r.ImageId).Distinct(StringComparer.Ordinal))
        {
            var tags = all.Where(r => r.ImageId == imageId && r.IsTagged).Select(r => r.Key).ToList();
            var chosenTags = tags.Where(chosen.Contains).ToList();
            if (tags.Count <= 1)
                items.Add(new RemovalItem(imageId));
            else
                items.Add(new RemovalItem(imageId, chosenTags.ToImmutableArray()));

            if (tags.Count <= 1 || chosenTags.Count == tags.Count)
            {
                deleted.Add(imageId);
                // Only a delete is refused while a container uses the image; removing one of its tags is not.
                if (confirmed.First(r => r.ImageId == imageId) is { InUse: true } row)
                    deletedInUse.Add(row);
            }
            else
            {
                untagOnly = true;
            }
        }

        ImageDiskUsage? UsageOf(string id) => usage?.Images.FirstOrDefault(i => i.Id == id);
        var freed = Freed(deleted, id => UsageOf(id)?.UniqueSize);
        var shared = deleted.Count == 1 ? UsageOf(deleted[0])?.SharedSize : null;
        string title, text;
        if (confirmed.Count == 1)
        {
            var row = confirmed[0];
            (title, text) = deleted.Count == 0
                ? ("Remove tag?", $"The tag {row.DisplayName} will be removed. The image keeps its other tags.")
                : ("Remove image?", $"{row.DisplayName} will be deleted from this engine.");
        }
        else
        {
            title = items.Count == 1 ? "Remove image?" : $"Remove {items.Count} images?";
            text = Listed("These will be removed:", confirmed.Select(r => r.DisplayName));
            if (untagOnly)
                text += "\nImages that keep other tags lose only the tag.";
        }

        text = WithInUse(text, confirmed.Count == 1, deletedInUse.Select(r => (r.DisplayName, r.UsedBy)));
        return new RemovalPlan(ResourceKind.Images, items.ToImmutable(), title, WithFreed(text, freed, several: deleted.Count > 1, shared));
    }

    /// <summary>Deleting the volumes <paramref name="keys"/> among <paramref name="shown"/>; says the data in them is lost and, when known, the space freed.</summary>
    public static RemovalPlan RemoveVolumes(IReadOnlyList<VolumeRow> shown, IEnumerable<string> keys, bool connected)
    {
        var confirmed = RowSelection.Confirmed(shown, keys, connected);
        var title = confirmed.Count == 1 ? "Remove volume?" : $"Remove {confirmed.Count} volumes?";
        var text = confirmed.Count == 1
            ? $"{confirmed[0].Name} will be removed. {VolumeDataLostOne}"
            : Listed($"{confirmed.Count} volumes will be removed:", confirmed.Select(r => r.Name)) + "\n" + VolumeDataLostMany;
        text = WithInUse(text, confirmed.Count == 1, confirmed.Where(r => r.InUse).Select(r => (r.Name, r.UsedBy)));
        var freed = Freed(confirmed, r => r.Size);
        return new RemovalPlan(ResourceKind.Volumes, [.. confirmed.Select(r => new RemovalItem(r.Name))], title, WithFreed(text, freed));
    }

    /// <summary>
    /// Deleting the networks <paramref name="keys"/> among <paramref name="shown"/>; a built-in network is never sent. Names the containers
    /// attached to them, which lose the network (the engine does not refuse when they are stopped), or says they are not known while
    /// this connection's container list is not (<paramref name="containersKnown"/> false: see <see cref="UnusedNotKnown(EngineState)"/>).
    /// </summary>
    public static RemovalPlan RemoveNetworks(IReadOnlyList<NetworkRow> shown, IEnumerable<string> keys, bool connected, bool containersKnown)
    {
        var confirmed = RowSelection.Confirmed(shown, keys, connected, r => !r.IsBuiltIn);
        var title = confirmed.Count == 1 ? "Remove network?" : $"Remove {confirmed.Count} networks?";
        var text = confirmed.Count == 1
            ? $"{confirmed[0].Name} will be removed."
            : Listed($"{confirmed.Count} networks will be removed:", confirmed.Select(r => r.Name));

        // The engine removes a network whose containers are stopped without a word: say who loses it, or that it is not known.
        if (!containersKnown && confirmed.Count > 0)
            text += "\nThe container list has not been read, so the containers attached are not known.";
        var attached = confirmed.SelectMany(r => r.Containers).Distinct(StringComparer.Ordinal).ToList();
        if (attached.Count > 0)
        {
            var heading = confirmed.Count == 1
                ? "These containers are attached and will lose this network:"
                : "These containers are attached and will lose these networks:";
            text += "\n" + Listed(heading, attached);
        }

        return new RemovalPlan(ResourceKind.Networks, [.. confirmed.Select(r => new RemovalItem(r.Id))], title, text);
    }

    /// <summary>
    /// "Remove unused images": every image no container uses, or with <paramref name="danglingOnly"/> only the untagged ones, each named by
    /// its tags. An image with several tags is sent as its tags in order (the last deletes it), any other by its ID, as a delete does. Pending images are left out. The space freed
    /// is the engine's reclaimable figure for all unused images, or the sum of the dangling images' own sizes.
    /// </summary>
    /// <param name="listNarrowed">The page's search or filter hides rows: the dialog says this is every unused image on the engine.</param>
    public static PrunePlan PruneImages(EngineState state, bool danglingOnly, bool listNarrowed = false) =>
        PruneImages(state, danglingOnly, only: null, listNarrowed);

    /// <summary>"Remove unused volumes": every volume no container mounts, named or anonymous, named as the list shows it; warns that the data is lost.</summary>
    /// <param name="listNarrowed">The page's search or filter hides rows: the dialog says this is every unused volume on the engine.</param>
    public static PrunePlan PruneVolumes(EngineState state, bool listNarrowed = false) => PruneVolumes(state, only: null, listNarrowed);

    /// <summary>
    /// "Remove unused networks": every network no container of any state is attached to (the engine itself would remove one only stopped
    /// containers use), other than the built-in ones.
    /// </summary>
    /// <param name="listNarrowed">The page's search or filter hides rows: the dialog says this is every unused network on the engine.</param>
    public static PrunePlan PruneNetworks(EngineState state, bool listNarrowed = false) => PruneNetworks(state, only: null, listNarrowed);

    /// <summary>
    /// Why what is unused is not known now, or null when it is: a container of any state keeps an image, volume or network in use, so it
    /// is known only from this connection's container list, once read and while its last read did not fail.
    /// </summary>
    public static string? UnusedNotKnown(EngineState state) =>
        state.ContainersLoadError is not null ? "The container list could not be read, so what is in use is not known."
        : !state.ListLoaded ? "Waiting for the container list, which tells what is in use."
        : null;

    /// <summary>
    /// Why what of <paramref name="kind"/> is unused is not known now, or null when it is: the container list must be known
    /// (<see cref="UnusedNotKnown(EngineState)"/>), and so must the kind's own list: read on this connection, and its last read not failed.
    /// </summary>
    public static string? UnusedNotKnown(EngineState state, ResourceKind kind)
    {
        if (UnusedNotKnown(state) is { } containers)
            return containers;

        var (list, noun) = kind switch
        {
            ResourceKind.Images => (state.Images.Status, "image"),
            ResourceKind.Volumes => (state.Volumes.Status, "volume"),
            _ => (state.Networks.Status, "network"),
        };
        return list.LoadError is not null ? $"The {noun} list could not be read, so what is unused is not known."
            : !list.Loaded ? $"Waiting for the {noun} list."
            : null;
    }

    /// <summary>
    /// What a "Remove unused …" dialog sends when the user confirms: of what <paramref name="named"/> named, what <paramref name="state"/>
    /// still lists, no container uses and nothing is pending on, in the order named; an image with its tags as they are now. Nothing new
    /// is added, and nothing at all while not connected or while what is unused is not known
    /// (<see cref="UnusedNotKnown(EngineState, ResourceKind)"/>).
    /// </summary>
    public static PrunePlan ConfirmPrune(PrunePlan named, EngineState state)
    {
        var only = new Only(named.Items.Select(i => i.Id));
        return named.Kind switch
        {
            ResourceKind.Images => PruneImages(state, named.DanglingImagesOnly, only, listNarrowed: false),
            ResourceKind.Volumes => PruneVolumes(state, only, listNarrowed: false),
            _ => PruneNetworks(state, only, listNarrowed: false),
        };
    }

    private static PrunePlan PruneImages(EngineState state, bool danglingOnly, Only? only, bool listNarrowed)
    {
        var unknown = UnusedNotKnown(state, ResourceKind.Images);
        var canPlan = state.Status == ConnectionStatus.Connected && unknown is null;
        var items = ImageRows.From(state)
            .GroupBy(r => r.ImageId, StringComparer.Ordinal)
            .Where(g => canPlan && (only?.Has(g.Key) ?? true))
            .Select(g => g.ToList())
            .Where(rows => !rows[0].InUse && !rows[0].IsPending && (!danglingOnly || rows[0].IsDangling))
            .Select(rows => new PlannedRemoval(
                rows[0].ImageId,
                string.Join(", ", rows.Select(r => r.DisplayName)),
                Unused(rows[0].ImageId, rows.Count > 1 ? [.. rows.Select(r => r.Key)] : [rows[0].ImageId])))
            .OrderBy(i => only?.IndexOf(i.Id) ?? 0)
            .ToImmutableArray();
        var usage = state.DiskUsage.Usage;
        // The engine's reclaimable figure covers every unused image; a sum of unique sizes is a floor (layers shared only among them).
        var summed = danglingOnly || only is not null;
        var freed = usage is null ? null
            : summed ? Freed(items, i => usage.Images.FirstOrDefault(u => u.Id == i.Id)?.UniqueSize)
            : usage.ImageTotals.Reclaimable;
        var several = summed && items.Length > 1;
        return danglingOnly
            ? Prune(ResourceKind.Images, true, items, freed, several, "Remove dangling images?", "untagged image", unknown ?? "No image is dangling.", listNarrowed, warning: false)
            : Prune(ResourceKind.Images, false, items, freed, several, "Remove unused images?", "image", unknown ?? "No image is unused.", listNarrowed, warning: false);
    }

    private static PrunePlan PruneVolumes(EngineState state, Only? only, bool listNarrowed)
    {
        var unknown = UnusedNotKnown(state, ResourceKind.Volumes);
        var canPlan = state.Status == ConnectionStatus.Connected && unknown is null;
        var rows = VolumeRows.From(state)
            .Where(r => canPlan && !r.InUse && !r.IsPending && (only?.Has(r.Name) ?? true))
            .OrderBy(r => only?.IndexOf(r.Name) ?? 0)
            .ToList();
        var items = rows.Select(r => new PlannedRemoval(r.Name, r.ShownName, Unused(r.Name, [r.Name]))).ToImmutableArray();
        var usage = state.DiskUsage.Usage;
        var freed = usage is null ? null : only is null ? usage.VolumeTotals.Reclaimable : Freed(rows, r => r.Size);
        return Prune(ResourceKind.Volumes, false, items, freed, several: false, "Remove unused volumes?", "volume", unknown ?? "No volume is unused.", listNarrowed, warning: true);
    }

    private static PrunePlan PruneNetworks(EngineState state, Only? only, bool listNarrowed)
    {
        var unknown = UnusedNotKnown(state, ResourceKind.Networks);
        var canPlan = state.Status == ConnectionStatus.Connected && unknown is null;
        var items = NetworkRows.From(state)
            .Where(r => canPlan && !r.InUse && !r.IsBuiltIn && !r.IsPending && (only?.Has(r.Id) ?? true))
            .OrderBy(r => only?.IndexOf(r.Id) ?? 0)
            .Select(r => new PlannedRemoval(r.Id, r.Name, Unused(r.Id, [r.Id])))
            .ToImmutableArray();
        return Prune(ResourceKind.Networks, false, items, null, several: false, "Remove unused networks?", "network", unknown ?? "No network is unused.", listNarrowed, warning: false);
    }

    /// <param name="several">The freed figure is a sum over several items' own sizes, so a floor.</param>
    /// <param name="listNarrowed">The page's search or filter hides rows, so the dialog says it covers the whole engine.</param>
    /// <param name="warning">Whether to warn that the data in what is removed is lost (volumes).</param>
    private static PrunePlan Prune(
        ResourceKind kind,
        bool danglingOnly,
        ImmutableArray<PlannedRemoval> items,
        long? freed,
        bool several,
        string title,
        string noun,
        string empty,
        bool listNarrowed,
        bool warning)
    {
        if (items.IsEmpty)
            return new PrunePlan(kind, danglingOnly, items, null, title, empty);

        // Every name, however many: this dialog is the only place that names them.
        var heading = items.Length == 1 ? $"1 {noun} that no container uses will be removed:" : $"{items.Length} {noun}s that no container uses will be removed:";
        var text = string.Join("\n", [heading, .. items.Select(i => i.Name)]);
        if (listNarrowed)
            text += $"\nThis is every unused {(danglingOnly ? "untagged image" : noun)} on the engine, not only the rows shown.";
        if (warning)
            text += "\n" + (items.Length == 1 ? VolumeDataLostOne : VolumeDataLostMany);
        return new PrunePlan(kind, danglingOnly, items, freed, title, WithFreed(text, freed, several, shared: null));
    }

    /// <summary>The IDs a dialog named, in its order: at confirm, only these are kept, in this order.</summary>
    private sealed class Only(IEnumerable<string> ids)
    {
        private readonly Dictionary<string, int> _index = ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i, StringComparer.Ordinal);

        public bool Has(string id) => _index.ContainsKey(id);

        public int IndexOf(string id) => _index[id];
    }

    /// <summary>A "Remove unused" item: the session skips it if a container uses it by the time its turn comes.</summary>
    private static RemovalItem Unused(string id, ImmutableArray<string> keys) => new(id, keys, OnlyIfUnused: true);

    private static string Listed(string heading, IEnumerable<string> names) =>
        string.Join("\n", [heading, .. RowSelection.NameLines([.. names])]);

    /// <summary>
    /// Adds what the engine frees: <paramref name="freed"/> is the deleted items' own sizes (for images, what no other image shares), so
    /// with <paramref name="several"/> it is a floor ("At least"), and an image's <paramref name="shared"/> part is said to stay.
    /// </summary>
    private static string WithFreed(string text, long? freed, bool several = false, long? shared = null)
    {
        if (freed is not { } bytes || bytes <= 0)
            return text;
        var line = several ? $"At least {ByteFormat.Bytes(bytes)} will be freed." : $"{ByteFormat.Bytes(bytes)} will be freed.";
        if (!several && shared is { } kept and > 0)
            line = $"{ByteFormat.Bytes(bytes)} will be freed; {ByteFormat.Bytes(kept)} is shared with other images and stays.";
        return $"{text}\n{line}";
    }

    /// <summary>
    /// Adds which of the items a container uses, by name: the engine refuses to remove those (nothing is forced). <paramref name="one"/>:
    /// the dialog names a single item, so the line need not repeat it.
    /// </summary>
    private static string WithInUse(string text, bool one, IEnumerable<(string Name, IReadOnlyList<string> UsedBy)> inUse)
    {
        var used = inUse.ToList();
        if (used.Count == 0)
            return text;
        if (one)
            return $"{text}\nUsed by {string.Join(", ", used[0].UsedBy)}. The engine will refuse while a container uses it.";
        return Listed($"{text}\nIn use, so the engine will refuse them while a container uses them:", used.Select(u => $"{u.Name} (used by {string.Join(", ", u.UsedBy)})"));
    }

    /// <summary>The sum of the sizes, or null when there is nothing or any size is unknown.</summary>
    private static long? Freed<T>(IEnumerable<T> items, Func<T, long?> size)
    {
        long total = 0;
        var any = false;
        foreach (var item in items)
        {
            if (size(item) is not { } bytes)
                return null;
            total += bytes;
            any = true;
        }

        return any ? total : null;
    }
}

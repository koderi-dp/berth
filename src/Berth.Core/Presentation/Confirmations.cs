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
/// What a delete or "Remove unused …" of images, volumes or networks sends, and the confirmation that names it: at most
/// <see cref="RowSelection.NamedInText"/> items, then "and N more", and the space freed when it is known. Nothing is ever forced: the
/// engine's refusal of something in use is shown as it is.
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
        var untagOnly = false;
        foreach (var imageId in confirmed.Select(r => r.ImageId).Distinct(StringComparer.Ordinal))
        {
            var tags = all.Where(r => r.ImageId == imageId && r.IsTagged).Select(r => r.Key).ToList();
            var chosenTags = tags.Where(chosen.Contains).ToList();
            if (tags.Count <= 1)
            {
                items.Add(new RemovalItem(imageId));
                deleted.Add(imageId);
                continue;
            }

            items.Add(new RemovalItem(chosenTags.ToImmutableArray()));
            if (chosenTags.Count == tags.Count)
                deleted.Add(imageId);
            else
                untagOnly = true;
        }

        var freed = Freed(deleted, id => usage?.Images.FirstOrDefault(i => i.Id == id)?.UniqueSize);
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

        return new RemovalPlan(ResourceKind.Images, items.ToImmutable(), title, WithFreed(text, freed));
    }

    /// <summary>Deleting the volumes <paramref name="keys"/> among <paramref name="shown"/>; says the data in them is lost and, when known, the space freed.</summary>
    public static RemovalPlan RemoveVolumes(IReadOnlyList<VolumeRow> shown, IEnumerable<string> keys, bool connected)
    {
        var confirmed = RowSelection.Confirmed(shown, keys, connected);
        var title = confirmed.Count == 1 ? "Remove volume?" : $"Remove {confirmed.Count} volumes?";
        var text = confirmed.Count == 1
            ? $"{confirmed[0].Name} will be removed. {VolumeDataLostOne}"
            : Listed($"{confirmed.Count} volumes will be removed:", confirmed.Select(r => r.Name)) + "\n" + VolumeDataLostMany;
        var freed = Freed(confirmed, r => r.Size);
        return new RemovalPlan(ResourceKind.Volumes, [.. confirmed.Select(r => new RemovalItem(r.Name))], title, WithFreed(text, freed));
    }

    /// <summary>Deleting the networks <paramref name="keys"/> among <paramref name="shown"/>; a built-in network is never sent.</summary>
    public static RemovalPlan RemoveNetworks(IReadOnlyList<NetworkRow> shown, IEnumerable<string> keys, bool connected)
    {
        var confirmed = RowSelection.Confirmed(shown, keys, connected, r => !r.IsBuiltIn);
        var title = confirmed.Count == 1 ? "Remove network?" : $"Remove {confirmed.Count} networks?";
        var text = confirmed.Count == 1
            ? $"{confirmed[0].Name} will be removed."
            : Listed($"{confirmed.Count} networks will be removed:", confirmed.Select(r => r.Name));
        return new RemovalPlan(ResourceKind.Networks, [.. confirmed.Select(r => new RemovalItem(r.Id))], title, text);
    }

    /// <summary>
    /// "Remove unused images": every image no container uses, or with <paramref name="danglingOnly"/> only the untagged ones, each named by
    /// its tags and sent as its tags in order (the last deletes it), or its ID when untagged. Pending images are left out. The space freed
    /// is the engine's reclaimable figure for all unused images, or the sum of the dangling images' own sizes.
    /// </summary>
    public static PrunePlan PruneImages(EngineState state, bool danglingOnly) => PruneImages(state, danglingOnly, only: null);

    /// <summary>"Remove unused volumes": every volume no container mounts, named or anonymous, named as the list shows it; warns that the data is lost.</summary>
    public static PrunePlan PruneVolumes(EngineState state) => PruneVolumes(state, only: null);

    /// <summary>
    /// "Remove unused networks": every network no container of any state is attached to (the engine itself would remove one only stopped
    /// containers use), other than the built-in ones.
    /// </summary>
    public static PrunePlan PruneNetworks(EngineState state) => PruneNetworks(state, only: null);

    /// <summary>
    /// What a "Remove unused …" dialog sends when the user confirms: of what <paramref name="named"/> named, what <paramref name="state"/>
    /// still lists, no container uses and nothing is pending on, in the order named; an image with its tags as they are now. Nothing new
    /// is added, and nothing at all while not connected.
    /// </summary>
    public static PrunePlan ConfirmPrune(PrunePlan named, EngineState state)
    {
        var only = new Only(named.Items.Select(i => i.Id));
        return named.Kind switch
        {
            ResourceKind.Images => PruneImages(state, named.DanglingImagesOnly, only),
            ResourceKind.Volumes => PruneVolumes(state, only),
            _ => PruneNetworks(state, only),
        };
    }

    private static PrunePlan PruneImages(EngineState state, bool danglingOnly, Only? only)
    {
        var connected = state.Status == ConnectionStatus.Connected;
        var items = ImageRows.From(state)
            .GroupBy(r => r.ImageId, StringComparer.Ordinal)
            .Where(g => connected && (only?.Has(g.Key) ?? true))
            .Select(g => g.ToList())
            .Where(rows => !rows[0].InUse && !rows[0].IsPending && (!danglingOnly || rows[0].IsDangling))
            .Select(rows => new PlannedRemoval(
                rows[0].ImageId,
                string.Join(", ", rows.Select(r => r.DisplayName)),
                rows[0].IsTagged ? new RemovalItem(rows.Select(r => r.Key).ToImmutableArray()) : new RemovalItem(rows[0].ImageId)))
            .OrderBy(i => only?.IndexOf(i.Id) ?? 0)
            .ToImmutableArray();
        var usage = state.DiskUsage.Usage;
        var freed = usage is null ? null
            : danglingOnly || only is not null ? Freed(items, i => usage.Images.FirstOrDefault(u => u.Id == i.Id)?.UniqueSize)
            : usage.ImageTotals.Reclaimable;
        return danglingOnly
            ? Prune(ResourceKind.Images, true, items, freed, "Remove dangling images?", "untagged image", "No image is dangling.", warning: false)
            : Prune(ResourceKind.Images, false, items, freed, "Remove unused images?", "image", "No image is unused.", warning: false);
    }

    private static PrunePlan PruneVolumes(EngineState state, Only? only)
    {
        var connected = state.Status == ConnectionStatus.Connected;
        var rows = VolumeRows.From(state)
            .Where(r => connected && !r.InUse && !r.IsPending && (only?.Has(r.Name) ?? true))
            .OrderBy(r => only?.IndexOf(r.Name) ?? 0)
            .ToList();
        var items = rows.Select(r => new PlannedRemoval(r.Name, r.ShownName, new RemovalItem(r.Name))).ToImmutableArray();
        var usage = state.DiskUsage.Usage;
        var freed = usage is null ? null : only is null ? usage.VolumeTotals.Reclaimable : Freed(rows, r => r.Size);
        return Prune(ResourceKind.Volumes, false, items, freed, "Remove unused volumes?", "volume", "No volume is unused.", warning: true);
    }

    private static PrunePlan PruneNetworks(EngineState state, Only? only)
    {
        var connected = state.Status == ConnectionStatus.Connected;
        var items = NetworkRows.From(state)
            .Where(r => connected && !r.InUse && !r.IsBuiltIn && !r.IsPending && (only?.Has(r.Id) ?? true))
            .OrderBy(r => only?.IndexOf(r.Id) ?? 0)
            .Select(r => new PlannedRemoval(r.Id, r.Name, new RemovalItem(r.Id)))
            .ToImmutableArray();
        return Prune(ResourceKind.Networks, false, items, null, "Remove unused networks?", "network", "No network is unused.", warning: false);
    }

    /// <param name="warning">Whether to warn that the data in what is removed is lost (volumes).</param>
    private static PrunePlan Prune(
        ResourceKind kind, bool danglingOnly, ImmutableArray<PlannedRemoval> items, long? freed, string title, string noun, string empty, bool warning)
    {
        if (items.IsEmpty)
            return new PrunePlan(kind, danglingOnly, items, null, title, empty);

        var heading = items.Length == 1 ? $"1 {noun} that no container uses will be removed:" : $"{items.Length} {noun}s that no container uses will be removed:";
        var text = Listed(heading, items.Select(i => i.Name));
        if (warning)
            text += "\n" + (items.Length == 1 ? VolumeDataLostOne : VolumeDataLostMany);
        return new PrunePlan(kind, danglingOnly, items, freed, title, WithFreed(text, freed));
    }

    /// <summary>The IDs a dialog named, in its order: at confirm, only these are kept, in this order.</summary>
    private sealed class Only(IEnumerable<string> ids)
    {
        private readonly Dictionary<string, int> _index = ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i, StringComparer.Ordinal);

        public bool Has(string id) => _index.ContainsKey(id);

        public int IndexOf(string id) => _index[id];
    }

    private static string Listed(string heading, IEnumerable<string> names) =>
        string.Join("\n", [heading, .. RowSelection.NameLines([.. names])]);

    private static string WithFreed(string text, long? freed) =>
        freed is { } bytes and > 0 ? $"{text}\n{ByteFormat.Bytes(bytes)} will be freed." : text;

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

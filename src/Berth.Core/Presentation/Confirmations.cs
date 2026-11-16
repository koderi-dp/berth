using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Session;
using Berth.State;

namespace Berth.Presentation;

/// <summary>
/// A confirmed remove: exactly what is sent (<see cref="Keys"/>) and the dialog that names it. Build it from the current state both
/// when the dialog opens (for its text) and when the user confirms (for what is sent).
/// </summary>
public sealed record RemovalPlan(ResourceKind Kind, ImmutableArray<string> Keys, string Title, string Text)
{
    /// <summary>Nothing is left to send (the rows went away, became pending, or the engine is not connected).</summary>
    public bool IsEmpty => Keys.IsEmpty;

    /// <summary>The event that sends <see cref="Keys"/>.</summary>
    public SessionEvent ToEvent() => Kind switch
    {
        ResourceKind.Images => new RemoveImagesRequested(Keys),
        ResourceKind.Volumes => new RemoveVolumesRequested(Keys),
        _ => new RemoveNetworksRequested(Keys),
    };
}

/// <summary>
/// A prune and the dialog that names what it removes: everything of its kind that no container uses, as the lists show it now, and
/// the space freed when the disk use tells it.
/// </summary>
public sealed record PrunePlan(ResourceKind Kind, bool DanglingImagesOnly, IReadOnlyList<string> Names, long? SpaceFreed, string Title, string Text)
{
    /// <summary>There is nothing to prune; the dialog's text says so and its confirm button stays disabled.</summary>
    public bool IsEmpty => Names.Count == 0;

    public SessionEvent ToEvent() => new PruneRequested(Kind, DanglingImagesOnly);
}

/// <summary>
/// What a remove or prune of images, volumes or networks sends, and the confirmation that names it: at most
/// <see cref="RowSelection.NamedInText"/> items, then "and N more", and the space freed when it is known.
/// </summary>
public static class Confirmations
{
    private const string VolumeDataLostOne = "The data in it is lost for good.";
    private const string VolumeDataLostMany = "The data in them is lost for good.";

    /// <summary>
    /// Removing the rows <paramref name="keys"/> of <paramref name="rows"/> (the image list as it is now): a tag of an image that keeps
    /// other tags removes that tag; the image's only tag, or an untagged image, removes the image by ID; every tag of an image removes
    /// each tag, so the last one deletes the image without forcing. Never forced: the engine's refusal of an image in use is shown as
    /// it is. <paramref name="usage"/>, when read, tells the space the deleted images free.
    /// </summary>
    public static RemovalPlan RemoveImages(IReadOnlyList<ImageRow> rows, IEnumerable<string> keys, bool connected, DiskUsage? usage)
    {
        var confirmed = RowSelection.Confirmed(rows, keys, connected);
        var keysSent = ImmutableArray.CreateBuilder<string>();
        var deleted = new List<string>();
        var untagOnly = false;
        foreach (var row in confirmed)
        {
            if (!row.IsTagged || row.TagCount == 1)
            {
                keysSent.Add(row.ImageId);
                deleted.Add(row.ImageId);
            }
            else
            {
                keysSent.Add(row.Key);
                var everyTag = rows.Where(r => r.ImageId == row.ImageId).All(r => confirmed.Contains(r));
                if (everyTag && !deleted.Contains(row.ImageId))
                    deleted.Add(row.ImageId);
                untagOnly |= !everyTag;
            }
        }

        var freed = Freed(deleted, id => usage?.Images.FirstOrDefault(i => i.Id == id)?.UniqueSize);
        var title = confirmed.Count == 1 ? "Remove image?" : $"Remove {confirmed.Count} images?";
        string text;
        if (confirmed.Count == 1)
        {
            var row = confirmed[0];
            text = deleted.Count == 0
                ? $"The tag {row.DisplayName} will be removed. The image keeps its other tags."
                : $"{row.DisplayName} will be deleted from this engine.";
        }
        else
        {
            text = Listed($"{confirmed.Count} images will be removed:", confirmed.Select(r => r.DisplayName));
            if (untagOnly)
                text += "\nImages that keep other tags lose only the tag.";
        }

        return new RemovalPlan(ResourceKind.Images, keysSent.ToImmutable(), title, WithFreed(text, freed));
    }

    /// <summary>Removing the volumes <paramref name="keys"/>; says the data in them is lost and, when known, the space freed.</summary>
    public static RemovalPlan RemoveVolumes(IReadOnlyList<VolumeRow> rows, IEnumerable<string> keys, bool connected)
    {
        var confirmed = RowSelection.Confirmed(rows, keys, connected);
        var title = confirmed.Count == 1 ? "Remove volume?" : $"Remove {confirmed.Count} volumes?";
        var text = confirmed.Count == 1
            ? $"{confirmed[0].Name} will be removed. {VolumeDataLostOne}"
            : Listed($"{confirmed.Count} volumes will be removed:", confirmed.Select(r => r.Name)) + "\n" + VolumeDataLostMany;
        var freed = Freed(confirmed, r => r.Size);
        return new RemovalPlan(ResourceKind.Volumes, [.. confirmed.Select(r => r.Name)], title, WithFreed(text, freed));
    }

    /// <summary>Removing the networks <paramref name="keys"/>; a built-in network is never sent.</summary>
    public static RemovalPlan RemoveNetworks(IReadOnlyList<NetworkRow> rows, IEnumerable<string> keys, bool connected)
    {
        var confirmed = RowSelection.Confirmed(rows, keys, connected, r => !r.IsBuiltIn);
        var title = confirmed.Count == 1 ? "Remove network?" : $"Remove {confirmed.Count} networks?";
        var text = confirmed.Count == 1
            ? $"{confirmed[0].Name} will be removed."
            : Listed($"{confirmed.Count} networks will be removed:", confirmed.Select(r => r.Name));
        return new RemovalPlan(ResourceKind.Networks, [.. confirmed.Select(r => r.Id)], title, text);
    }

    /// <summary>
    /// Pruning images: every image no container uses, or with <paramref name="danglingOnly"/> only the untagged ones, each named by its
    /// tags. The space freed is the engine's reclaimable figure for all unused images, or the sum of the dangling images' own sizes.
    /// </summary>
    public static PrunePlan PruneImages(EngineState state, bool danglingOnly)
    {
        var images = ImageRows.From(state)
            .GroupBy(r => r.ImageId, StringComparer.Ordinal)
            .Where(g => !g.First().InUse && (!danglingOnly || g.First().IsDangling))
            .Select(g => (Id: g.Key, Name: string.Join(", ", g.Select(r => r.DisplayName))))
            .ToList();
        var usage = state.DiskUsage.Usage;
        var freed = usage is null ? null
            : danglingOnly ? Freed(images, i => usage.Images.FirstOrDefault(u => u.Id == i.Id)?.UniqueSize)
            : usage.ImageTotals.Reclaimable;

        var noun = danglingOnly ? "untagged image" : "image";
        var title = danglingOnly ? "Remove dangling images?" : "Remove unused images?";
        var empty = danglingOnly ? "No image is dangling." : "No image is unused.";
        return Prune(ResourceKind.Images, danglingOnly, [.. images.Select(i => i.Name)], freed, title, noun, empty, warning: null);
    }

    /// <summary>Pruning volumes: every volume no container mounts, named or anonymous; warns that their data is lost.</summary>
    public static PrunePlan PruneVolumes(EngineState state)
    {
        var names = VolumeRows.From(state).Where(r => !r.InUse).Select(r => r.Name).ToList();
        return Prune(
            ResourceKind.Volumes, false, names, state.DiskUsage.Usage?.VolumeTotals.Reclaimable, "Remove unused volumes?", "volume", "No volume is unused.",
            VolumeDataLostMany);
    }

    /// <summary>Pruning networks: every network no container is attached to, other than the built-in ones.</summary>
    public static PrunePlan PruneNetworks(EngineState state)
    {
        var names = NetworkRows.From(state).Where(r => !r.InUse && !r.IsBuiltIn).Select(r => r.Name).ToList();
        return Prune(ResourceKind.Networks, false, names, null, "Remove unused networks?", "network", "No network is unused.", warning: null);
    }

    private static PrunePlan Prune(
        ResourceKind kind, bool danglingOnly, IReadOnlyList<string> names, long? freed, string title, string noun, string empty, string? warning)
    {
        if (names.Count == 0)
            return new PrunePlan(kind, danglingOnly, names, null, title, empty);

        var heading = names.Count == 1 ? $"1 {noun} that no container uses will be removed:" : $"{names.Count} {noun}s that no container uses will be removed:";
        var text = Listed(heading, names);
        if (warning is not null)
            text += "\n" + warning;
        return new PrunePlan(kind, danglingOnly, names, freed, title, WithFreed(text, freed));
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

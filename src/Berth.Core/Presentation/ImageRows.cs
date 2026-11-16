using Berth.Engine;
using Berth.Engine.Models;
using Berth.Session;
using Berth.State;

namespace Berth.Presentation;

/// <summary>Which images the Images page shows: all, those a container uses, those none uses, or the untagged ones.</summary>
public enum ImageShow { All, InUse, Unused, Dangling }

/// <summary>The columns of the images table that can be sorted by.</summary>
public enum ImageColumn { Name, Tag, Created, Size }

/// <summary>The Images page's search box and filter. The search matches a row's name, tag or image ID, ignoring case.</summary>
public sealed record ImageFilter(string Search, ImageShow Show)
{
    public static readonly ImageFilter All = new("", ImageShow.All);

    /// <summary>The search or filter may hide an unused image, so "Remove unused images" covers more than the rows shown.</summary>
    public bool MayHideUnused => Search.Trim().Length > 0 || Show is ImageShow.InUse or ImageShow.Dangling;
}

/// <summary>
/// One row of the images table: one tag of an image, or an untagged image.
/// </summary>
/// <param name="Key">The tag as the engine lists it ("nginx:latest"), or the image ID for an untagged image.</param>
/// <param name="Name">The repository ("nginx", "registry:5000/team/app"); for an untagged image its digest's repository, or "&lt;none&gt;".</param>
/// <param name="Tag">The tag, or "&lt;none&gt;".</param>
/// <param name="TagCount">How many tags the image has; a tag removed from an image with others leaves the image.</param>
/// <param name="UsedByCount">How many containers, in any state, use the image.</param>
/// <param name="UsedBy">The names of the containers that use the image, from the container list, by name.</param>
/// <param name="Pending">The action in progress on the image or on any of its tags, if any.</param>
public sealed record ImageRow(
    string Key,
    string ImageId,
    string ShortId,
    string Name,
    string Tag,
    bool IsTagged,
    int TagCount,
    DateTimeOffset Created,
    long Size,
    int UsedByCount,
    IReadOnlyList<string> UsedBy,
    ItemAction? Pending) : ISelectableRow
{
    /// <inheritdoc/>
    public bool IsPending => Pending is not null;

    /// <summary>A container, in any state, uses the image.</summary>
    public bool InUse => UsedByCount > 0;

    /// <summary>The image has no tag.</summary>
    public bool IsDangling => !IsTagged;

    /// <summary>How a confirmation names the row: "nginx:latest", or "&lt;none&gt;:&lt;none&gt; (0123456789ab)" for an untagged image.</summary>
    public string DisplayName => IsTagged ? Key : $"{Name}:{Tag} ({ShortId})";
}

/// <summary>Turns the engine's image list into the rows the Images page shows.</summary>
public static class ImageRows
{
    /// <summary>What the table shows for a missing repository or tag.</summary>
    public const string None = "<none>";

    /// <summary>Name, ascending.</summary>
    public static readonly ListSort<ImageColumn> DefaultSort = new(ImageColumn.Name, Descending: false);

    /// <summary>Every row of the state's image list, in the engine's order: one per tag, or one for an untagged image.</summary>
    public static IReadOnlyList<ImageRow> From(EngineState state)
    {
        var users = state.Containers
            .GroupBy(c => c.ImageId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(ComposeGrouping.NameOf).Order(StringComparer.OrdinalIgnoreCase)], StringComparer.Ordinal);
        var pending = state.Images.Pending;
        return [.. state.Images.Items.SelectMany(image => Rows(image, users.GetValueOrDefault(image.Id) ?? [], pending))];
    }

    /// <summary>The rows <paramref name="filter"/> keeps, in <paramref name="sort"/>'s order (ties by name, then tag).</summary>
    public static IReadOnlyList<ImageRow> Shown(IEnumerable<ImageRow> rows, ImageFilter filter, ListSort<ImageColumn> sort)
    {
        var term = filter.Search.Trim();
        bool Matches(ImageRow r) =>
            term.Length == 0 || Contains(r.Name, term) || Contains(r.Tag, term) || Contains(Hex(r.ImageId), term);
        bool Keeps(ImageRow r) => filter.Show switch
        {
            ImageShow.InUse => r.InUse,
            ImageShow.Unused => !r.InUse,
            ImageShow.Dangling => r.IsDangling,
            _ => true,
        };

        return sort.Apply(rows.Where(r => Matches(r) && Keeps(r)), Comparison, r => $"{r.Name}:{r.Tag}");
    }

    /// <summary>Why the table is empty: no images at all, nothing matching the search, or nothing the filter keeps.</summary>
    public static string EmptyText(bool engineHasImages, ImageFilter filter) =>
        !engineHasImages ? "No images on this engine."
        : !string.IsNullOrWhiteSpace(filter.Search) ? "No images match the search."
        : filter.Show switch
        {
            ImageShow.InUse => "No images are in use.",
            ImageShow.Unused => "No images are unused.",
            ImageShow.Dangling => "No dangling images.",
            _ => "No images match the search.",
        };

    /// <summary>
    /// The line above the table: "67 MB in use of 1.2 GB · 12 images" (in use is the engine's total less what removing the unused
    /// images would free), or "Calculating… · 12 images" until the disk use has been read. The count is of images, not tags.
    /// </summary>
    public static string UsageLine(EngineState state)
    {
        var count = state.Images.Items.Length;
        var images = count == 1 ? "1 image" : $"{count} images";
        if (state.DiskUsage.Usage is not { } usage)
            return $"Calculating… · {images}";

        var totals = usage.ImageTotals;
        return $"{ByteFormat.Bytes(totals.TotalSize - totals.Reclaimable)} in use of {ByteFormat.Bytes(totals.TotalSize)} · {images}";
    }

    /// <summary>
    /// How a line about a remove names its key: an image ID still listed by its tags ("web:1, web:latest"), or as an untagged image
    /// ("&lt;none&gt;:&lt;none&gt; (0123456789ab)"); a tag, or an image no longer listed, as the key itself.
    /// </summary>
    public static string NameOfKey(EngineState state, string key)
    {
        var image = state.Images.Items.FirstOrDefault(i => i.Id == key);
        return image is null ? key
            : image.RepoTags.Count > 0 ? string.Join(", ", image.RepoTags)
            : $"{None}:{None} ({ComposeGrouping.ShortId(image.Id)})";
    }

    private static IEnumerable<ImageRow> Rows(ImageSummary image, IReadOnlyList<string> users, IReadOnlyDictionary<string, ItemAction> pending)
    {
        var shortId = ComposeGrouping.ShortId(image.Id);
        var created = DateTimeOffset.FromUnixTimeSeconds(image.Created);
        var usedBy = Math.Max(image.Containers ?? 0, users.Count);
        // A remove of any of its tags, or of the image, changes every row of it: none is sent another while one runs.
        var imagePending = image.RepoTags.Prepend(image.Id).Select(k => pending.TryGetValue(k, out var a) ? a : (ItemAction?)null).FirstOrDefault(a => a is not null);

        if (image.RepoTags.Count == 0)
        {
            var name = image.RepoDigests.Count > 0 ? DigestRepository(image.RepoDigests[0]) : None;
            yield return new ImageRow(image.Id, image.Id, shortId, name, None, IsTagged: false, 0, created, image.Size, usedBy, users, imagePending);
            yield break;
        }

        foreach (var tag in image.RepoTags)
        {
            var (name, tagName) = Split(tag);
            yield return new ImageRow(tag, image.Id, shortId, name, tagName, IsTagged: true, image.RepoTags.Count, created, image.Size, usedBy, users, imagePending);
        }
    }

    /// <summary>A listed tag ("host:5000/a/b:1") split into repository and tag as a pull would split it.</summary>
    private static (string Name, string Tag) Split(string repoTag)
    {
        try
        {
            var reference = ImageReference.Parse(repoTag);
            return (reference.Name, reference.Tag);
        }
        catch (ArgumentException)
        {
            return (repoTag, None);
        }
    }

    private static string DigestRepository(string repoDigest)
    {
        var at = repoDigest.IndexOf('@', StringComparison.Ordinal);
        return at > 0 ? repoDigest[..at] : None;
    }

    private static Comparison<ImageRow> Comparison(ImageColumn column) => column switch
    {
        ImageColumn.Tag => (a, b) => SortText.Compare(a.Tag, b.Tag),
        ImageColumn.Created => (a, b) => a.Created.CompareTo(b.Created),
        ImageColumn.Size => (a, b) => a.Size.CompareTo(b.Size),
        _ => (a, b) => SortText.Compare(a.Name, b.Name),
    };

    /// <summary>An ID without "sha256:", so searching "sha" or "256" does not match every image.</summary>
    private static string Hex(string id) => id.StartsWith("sha256:", StringComparison.Ordinal) ? id["sha256:".Length..] : id;

    private static bool Contains(string text, string term) => text.Contains(term, StringComparison.OrdinalIgnoreCase);
}

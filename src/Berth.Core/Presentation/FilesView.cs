using System.Collections.Immutable;
using System.Globalization;
using Berth.Files;
using Berth.Session;

namespace Berth.Presentation;

/// <summary>
/// One row of the Files tab. <see cref="Shown"/> is the name on one line (a line break reads "⏎") and, for a link, "→ target";
/// <see cref="Opens"/>: double click or Enter opens it as a folder (a folder, or a link, which may lead to one).
/// </summary>
public sealed record FileRow(string Name, string Shown, FileKind Kind, ContainerPath Path, string SizeText, string ModifiedText, string Mode, bool Opens);

/// <summary>The Files table's sortable columns.</summary>
public enum FileColumn { Name, Size, Modified }

/// <summary>The Files tab's rows: folders first, then by name ignoring case (ties in ordinal order), with their texts.</summary>
public static class FileRows
{
    private static readonly Comparison<FileEntry> Order = (a, b) =>
    {
        var folders = (b.Kind == FileKind.Folder).CompareTo(a.Kind == FileKind.Folder);
        if (folders != 0)
            return folders;
        var name = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return name != 0 ? name : StringComparer.Ordinal.Compare(a.Name, b.Name);
    };

    /// <summary>
    /// The rows of <paramref name="listing"/>, times shown in <paramref name="zone"/>: folders first, then by <paramref name="sort"/>
    /// (by name ascending when none), ties by name.
    /// </summary>
    public static ImmutableArray<FileRow> Build(FileListing listing, TimeZoneInfo zone, ListSort<FileColumn>? sort = null)
    {
        IEnumerable<FileEntry> Sorted(IEnumerable<FileEntry> entries)
        {
            if (sort is null)
            {
                var list = entries.ToList();
                list.Sort(Order);
                return list;
            }

            return sort.Apply(entries, ByColumn, e => e.Name);
        }

        var folders = listing.Entries.Where(e => e.Kind == FileKind.Folder);
        var others = listing.Entries.Where(e => e.Kind != FileKind.Folder);
        return [.. Sorted(folders).Concat(Sorted(others)).Select(e => Row(listing.Path, e, zone))];
    }

    private static Comparison<FileEntry> ByColumn(FileColumn column) => column switch
    {
        FileColumn.Size => (a, b) => (a.Size ?? -1).CompareTo(b.Size ?? -1),
        FileColumn.Modified => (a, b) => Nullable.Compare(a.Modified, b.Modified),
        _ => (a, b) => SortText.Compare(a.Name, b.Name),
    };

    private static FileRow Row(ContainerPath folder, FileEntry entry, TimeZoneInfo zone)
    {
        var name = OneLine(entry.Name);
        var shown = entry is { Kind: FileKind.Link, LinkTarget: { } target } ? $"{name} → {OneLine(target)}" : name;
        var modified = entry.Modified is { } time
            ? TimeZoneInfo.ConvertTime(time, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "";
        return new FileRow(
            entry.Name,
            shown,
            entry.Kind,
            folder.Join(entry.Name),
            entry.Size is { } size ? ByteFormat.Bytes(size) : "",
            modified,
            entry.Mode,
            entry.Kind is FileKind.Folder or FileKind.Link);
    }

    private static string OneLine(string text) => text.Replace("\r", "␍", StringComparison.Ordinal).Replace("\n", "⏎", StringComparison.Ordinal);
}

/// <summary>
/// What the Files tab says around its table, for the state it is in. Each text is null when it does not apply.
/// </summary>
/// <param name="Loading">The folder is being read and no rows of it are known yet.</param>
/// <param name="Error">The listing failed: the engine's message as it is (or "… is not a folder.").</param>
/// <param name="Empty">The folder has no entries.</param>
/// <param name="WayNote">The listing was read through the archive.</param>
/// <param name="Note">Not every entry is shown, and why.</param>
/// <param name="Waiting">The page waits for the engine (the connection is lost or the session reopens).</param>
public sealed record FilesView(string? Loading, string? Error, string? Empty, string? WayNote, string? Note, string? Waiting)
{
    public const string ArchiveNote = "Listing through the archive: sizes of folders are not shown.";

    public static FilesView Of(FilesState files, bool waiting)
    {
        var listing = files.Listing;
        var loading = files.Status == FilesStatus.Loading && listing is null ? $"Reading {files.Path.Value}…" : null;
        var error = files.Status == FilesStatus.Failed ? files.Error : null;
        var empty = listing is { Entries.IsEmpty: true } && files.Status == FilesStatus.Loaded ? "This folder is empty." : null;
        var wayNote = listing?.Way == ListingWay.Archive ? ArchiveNote : null;
        var note = listing switch
        {
            { Capped: true } => $"Showing the first {FileListing.EntryLimit.ToString("N0", CultureInfo.InvariantCulture)} entries.",
            { Incomplete: true, Way: ListingWay.Archive } =>
                "The listing stopped early: this folder is too large to read through the archive. Some entries are not shown.",
            { Incomplete: true } => "Some entries could not be read.",
            _ => null,
        };
        return new FilesView(loading, error, empty, wayNote, note, waiting ? "Waiting for the engine…" : null);
    }

    /// <summary>The breadcrumb of <paramref name="path"/>: the root, then each folder down to it.</summary>
    public static IReadOnlyList<(string Name, ContainerPath Path)> Breadcrumb(ContainerPath path) => path.Segments;
}

/// <summary>A question asked before a transfer: the items it would replace, named, and a primary button; Cancel is the default.</summary>
public sealed record TransferConfirmation(string Title, string Text, IReadOnlyList<string> Names, string Primary);

/// <summary>What a download or an upload asks before it starts, when it would replace something.</summary>
public static class TransferConfirmations
{
    private const int NamesShown = 5;

    /// <summary>
    /// The question before a download of <paramref name="names"/> (the items' names in the container) into <paramref name="destination"/>,
    /// or null when nothing there has the name an item would be saved under (<see cref="WindowsNames"/>).
    /// </summary>
    public static TransferConfirmation? Download(IReadOnlyList<string> names, string destination, Func<string, bool> exists)
    {
        var there = names.Select(n => WindowsNames.Make(n).Name).Where(n => exists(Path.Combine(destination, n))).ToList();
        if (there.Count == 0)
            return null;
        var text = $"{List(there)} {(there.Count == 1 ? "is" : "are")} already in {destination}. Replace {(there.Count == 1 ? "it" : "them")}? "
            + "A folder that is already there keeps its other files.";
        return new TransferConfirmation($"Replace {Items(there.Count)}?", text, there, "Replace");
    }

    /// <summary>
    /// The question before an upload replaces <paramref name="names"/>: the names the engine said are in <paramref name="folder"/> just
    /// before sending (<see cref="UploadCheck"/>), never the listing on screen.
    /// </summary>
    public static TransferConfirmation Upload(IReadOnlyList<string> names, ContainerPath folder)
    {
        var text = $"{List(names)} {(names.Count == 1 ? "is" : "are")} already in {folder.Value}. Replace {(names.Count == 1 ? "it" : "them")}? "
            + "An upload cannot be undone.";
        return new TransferConfirmation($"Replace {Items(names.Count)} in {folder.Value}?", text, names, "Replace");
    }
    private static string Items(int count) => count == 1 ? "1 item" : $"{count} items";

    private static string List(IReadOnlyList<string> names)
    {
        var quoted = names.Take(NamesShown).ToList();
        var text = string.Join(", ", quoted);
        return names.Count > NamesShown ? $"{text} and {names.Count - NamesShown} more" : text;
    }
}

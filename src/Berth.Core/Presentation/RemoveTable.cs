using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.State;

namespace Berth.Presentation;

/// <summary>What a row of the remove table says in its Size cell.</summary>
public enum RemoveSize
{
    /// <summary>The disk use has not been read yet ("…").</summary>
    NotRead,

    /// <summary>The space it frees is known (<see cref="RemoveTableRow.Bytes"/>).</summary>
    Known,

    /// <summary>An image whose layers are all shared with other images: removing it frees (almost) nothing.</summary>
    Shared,

    /// <summary>An image that keeps other tags: only the tag goes, nothing is freed.</summary>
    TagOnly,

    /// <summary>The engine did not measure it.</summary>
    Unknown,
}

/// <summary>
/// One row of the remove table: one image (all the tags it names on one row), volume or network.
/// </summary>
/// <param name="Key">What the row's checkbox stands for: an image ID, a volume name, a network ID.</param>
/// <param name="SendKeys">For a delete, the list keys it sends (an image's checked tags, or its ID); for "Remove unused …", <paramref name="Key"/>.</param>
/// <param name="Name">The name shown (trimmed on screen).</param>
/// <param name="FullName">The whole name, for the tooltip.</param>
/// <param name="Second">The second column: an image's short ID, a volume's or network's driver.</param>
/// <param name="Scope">A network's scope; null for images and volumes, whose third column is their age.</param>
/// <param name="Created">When it was created, for an image's or volume's third column.</param>
/// <param name="InUseBy">A delete of something a container uses: who ("Used by web-1"); the engine refuses it.</param>
/// <param name="UntagOnly">A delete of some of an image's tags: the image keeps its others.</param>
/// <param name="Attached">A delete of a network: the containers attached to it, which lose it.</param>
public sealed record RemoveTableRow(
    string Key,
    ImmutableArray<string> SendKeys,
    string Name,
    string FullName,
    string Second,
    string? Scope,
    DateTimeOffset? Created,
    string? InUseBy = null,
    bool UntagOnly = false,
    IReadOnlyList<string>? Attached = null)
{
    /// <summary>What its Size cell says.</summary>
    public RemoveSize Size { get; init; } = RemoveSize.NotRead;

    /// <summary>The space it frees when <see cref="Size"/> is <see cref="RemoveSize.Known"/> (0 when shared or tag only).</summary>
    public long Bytes { get; init; }

    /// <summary>An image's part shared with other images, which stays; null when not known or none.</summary>
    public long? SharedBytes { get; init; }

    /// <summary>The Size cell's text.</summary>
    public string SizeText => Size switch
    {
        RemoveSize.Known => ByteFormat.Bytes(Bytes),
        RemoveSize.Shared => "Shared",
        RemoveSize.TagOnly => "Tag only",
        RemoveSize.Unknown => ByteFormat.Unknown,
        _ => "…",
    };

    /// <summary>The Size cell's tooltip: what the figure means.</summary>
    public string SizeTip => Size switch
    {
        RemoveSize.Known when SharedBytes is { } shared and > 0 => $"Frees {ByteFormat.Bytes(Bytes)}; {ByteFormat.Bytes(shared)} is shared with other images and stays.",
        RemoveSize.Known => $"Frees {ByteFormat.Bytes(Bytes)}.",
        RemoveSize.Shared => "Its layers are shared with other images, which keep them: removing it frees little or nothing.",
        RemoveSize.TagOnly => "The image keeps its other tags: only this tag is removed, nothing is freed.",
        RemoveSize.Unknown => "The engine did not measure it.",
        _ => "Reading the disk use",
    };

    /// <summary>The third column: a network's scope, or how long ago it was created.</summary>
    public string ThirdText(DateTimeOffset now) => Scope ?? AgeFormat.Ago(now, Created);
}

/// <summary>What the remove table says under it for the rows checked, and its confirm button.</summary>
/// <param name="Line">"18 of 22 selected · 640 MB will be freed".</param>
/// <param name="Notes">What the engine will refuse, which images lose only a tag, which containers lose a network; null when nothing.</param>
/// <param name="ConfirmText">"Remove 18 images"; "Remove" when nothing is checked.</param>
/// <param name="CanConfirm">Something is checked.</param>
public sealed record RemoveTableSummary(string Line, string? Notes, string ConfirmText, bool CanConfirm);

/// <summary>
/// The table a "Remove unused …" or a delete of several checked rows shows: a row per item with its size, each checked to start with; the
/// user unticks what to keep. Built once when the dialog opens (<see cref="Rows"/> is one list instance for the list control), its sizes
/// filled in from the disk use (<see cref="WithUsage"/>) once that is read. What is sent is decided at confirm from the rows still
/// checked: <see cref="Keys"/> for a delete, <see cref="PrunePlan.Keeping"/> for "Remove unused …", each planned again against the state then.
/// </summary>
public sealed record RemoveTable(ResourceKind Kind, string Lead, IReadOnlyList<RemoveTableRow> Rows)
{
    /// <summary>The second column's title.</summary>
    public string SecondTitle => Kind == ResourceKind.Images ? "Image ID" : "Driver";

    /// <summary>The third column's title.</summary>
    public string ThirdTitle => Kind == ResourceKind.Networks ? "Scope" : "Created";

    /// <summary>Whether the table has a Size column (not for networks).</summary>
    public bool HasSize => Kind != ResourceKind.Networks;

    /// <summary>The disk use has been read: sizes are known (or said to be unknown).</summary>
    public bool SizesRead { get; init; }

    /// <summary>The container list is known (for a delete of networks, which names the containers that lose them).</summary>
    public bool ContainersKnown { get; init; } = true;

    /// <summary>
    /// "Remove unused images" or "… volumes" of everything unused: with every row checked, the engine's own figure for what removing the
    /// unused ones frees is used rather than a sum of the rows' sizes.
    /// </summary>
    public bool EngineTotalWhenAll { get; init; }

    /// <summary>The engine's figure for removing every unused item, when <see cref="EngineTotalWhenAll"/> and the disk use is read.</summary>
    public long? EngineTotal { get; init; }

    /// <summary>Every row's key, the start selection: every row checked.</summary>
    public ImmutableHashSet<string> AllKeys => [.. Rows.Select(r => r.Key)];

    /// <summary>The header's select-all box: true when every row is checked, false when none, null when some.</summary>
    public bool? CheckedState(IReadOnlySet<string> chosen)
    {
        var on = Rows.Count(r => chosen.Contains(r.Key));
        return on == 0 ? false : on == Rows.Count ? true : null;
    }

    /// <summary>The list keys a delete sends for the rows checked, in the rows' order.</summary>
    public ImmutableArray<string> Keys(IReadOnlySet<string> chosen) => [.. Rows.Where(r => chosen.Contains(r.Key)).SelectMany(r => r.SendKeys)];

    /// <summary>The table with each row's size from <paramref name="usage"/> (the engine's disk use); unchanged while it is not read.</summary>
    public RemoveTable WithUsage(DiskUsage? usage)
    {
        if (usage is null || Kind == ResourceKind.Networks)
            return this;

        RemoveTableRow[] rows = [.. Rows.Select(r => Kind == ResourceKind.Images ? ImageSize(r, usage) : VolumeSize(r, usage))];
        var total = EngineTotalWhenAll ? (Kind == ResourceKind.Images ? usage.ImageTotals.Reclaimable : usage.VolumeTotals.Reclaimable) : (long?)null;
        return this with { Rows = rows, SizesRead = true, EngineTotal = total };
    }

    /// <summary>What the table says under it for the rows <paramref name="chosen"/>, and its confirm button.</summary>
    public RemoveTableSummary Summary(IReadOnlySet<string> chosen)
    {
        var picked = Rows.Where(r => chosen.Contains(r.Key)).ToList();
        var line = $"{picked.Count} of {Rows.Count} selected";
        if (HasSize && picked.Count > 0)
            line += " · " + Freed(picked);

        var noun = Kind switch { ResourceKind.Images => "image", ResourceKind.Volumes => "volume", _ => "network" };
        if (Kind == ResourceKind.Images && picked.Count > 0 && picked.All(r => r.UntagOnly))
            noun = "tag";
        var confirm = picked.Count == 0 ? "Remove" : picked.Count == 1 ? $"Remove 1 {noun}" : $"Remove {picked.Count} {noun}s";
        return new RemoveTableSummary(line, Notes(picked), confirm, picked.Count > 0);
    }

    private string Freed(List<RemoveTableRow> picked)
    {
        if (!SizesRead)
            return "sizes are not known yet";
        if (picked.Count == Rows.Count && EngineTotal is { } engine)
            return engine > 0 ? $"{ByteFormat.Bytes(engine)} will be freed" : "no space will be freed";
        if (picked.All(r => r.Size == RemoveSize.TagOnly))
            return "no space will be freed";
        if (picked.All(r => r.Size is RemoveSize.Unknown or RemoveSize.TagOnly))
            return "the space freed is not known";

        var bytes = picked.Sum(r => r.Bytes);
        // A sum of images' own sizes misses layers shared only among the images removed; a shared or unmeasured row adds what is not known.
        var about = picked.Any(r => r.Size is RemoveSize.Shared or RemoveSize.Unknown)
            || (Kind == ResourceKind.Images && picked.Count(r => r.Size == RemoveSize.Known) > 1);
        if (bytes <= 0)
            return "little or no space will be freed";
        return about ? $"about {ByteFormat.Bytes(bytes)} will be freed" : $"{ByteFormat.Bytes(bytes)} will be freed";
    }

    private string? Notes(List<RemoveTableRow> picked)
    {
        var lines = new List<string>();
        var inUse = picked.Where(r => r.InUseBy is not null && !r.UntagOnly).ToList();
        if (inUse.Count == 1)
            lines.Add($"{inUse[0].Name}: {inUse[0].InUseBy}. The engine will refuse while a container uses it.");
        else if (inUse.Count > 1)
            lines.Add("The engine will refuse these while a container uses them: " + Joined(inUse.Select(r => $"{r.Name} ({r.InUseBy})")) + ".");
        if (picked.Any(r => r.UntagOnly))
            lines.Add("Images that keep other tags lose only the tag.");
        if (Kind == ResourceKind.Networks && picked.Count > 0)
        {
            if (!ContainersKnown)
                lines.Add("The container list has not been read, so the containers attached are not known.");
            var attached = picked.SelectMany(r => r.Attached ?? []).Distinct(StringComparer.Ordinal).ToList();
            if (attached.Count > 0)
                lines.Add((picked.Count == 1 ? "These containers are attached and will lose the network: " : "These containers are attached and will lose these networks: ") + Joined(attached) + ".");
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string Joined(IEnumerable<string> names)
    {
        var all = names.ToList();
        return all.Count <= RowSelection.NamedInText
            ? string.Join(", ", all)
            : string.Join(", ", all.Take(RowSelection.NamedInText)) + $" and {all.Count - RowSelection.NamedInText} more";
    }

    private static RemoveTableRow ImageSize(RemoveTableRow row, DiskUsage usage)
    {
        if (row.UntagOnly)
            return row with { Size = RemoveSize.TagOnly, Bytes = 0, SharedBytes = null };
        var image = usage.Images.FirstOrDefault(i => i.Id == row.Key);
        if (image?.UniqueSize is not { } unique)
            return row with { Size = RemoveSize.Unknown, Bytes = 0, SharedBytes = null };
        var shared = image.SharedSize is { } s and > 0 ? s : (long?)null;
        return unique <= 0 && shared is not null
            ? row with { Size = RemoveSize.Shared, Bytes = 0, SharedBytes = shared }
            : row with { Size = RemoveSize.Known, Bytes = Math.Max(0, unique), SharedBytes = shared };
    }

    private static RemoveTableRow VolumeSize(RemoveTableRow row, DiskUsage usage) =>
        usage.Volumes.FirstOrDefault(v => v.Name == row.Key)?.Size is { } size and >= 0
            ? row with { Size = RemoveSize.Known, Bytes = size }
            : row with { Size = RemoveSize.Unknown, Bytes = 0 };
}

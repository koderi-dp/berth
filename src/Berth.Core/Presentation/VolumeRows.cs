using System.Text.RegularExpressions;
using Berth.Session;
using Berth.State;

namespace Berth.Presentation;

/// <summary>Which volumes or networks a page shows: all, those a container uses, or those none uses.</summary>
public enum UsageShow { All, InUse, Unused }

/// <summary>The columns of the volumes table that can be sorted by.</summary>
public enum VolumeColumn { Name, Driver, Created, Size }

/// <summary>The Volumes or Networks page's search box and filter. The search matches a name (and a network's driver or ID), ignoring case.</summary>
public sealed record UsageFilter(string Search, UsageShow Show)
{
    public static readonly UsageFilter All = new("", UsageShow.All);

    internal bool Keeps(bool inUse) => Show switch
    {
        UsageShow.InUse => inUse,
        UsageShow.Unused => !inUse,
        _ => true,
    };

    /// <summary>Why a table of <paramref name="items"/> ("volumes") is empty under this filter.</summary>
    internal string EmptyText(string items, bool engineHasAny) =>
        !engineHasAny ? $"No {items} on this engine."
        : !string.IsNullOrWhiteSpace(Search) ? $"No {items} match the search."
        : Show switch
        {
            UsageShow.InUse => $"No {items} are in use.",
            UsageShow.Unused => $"No {items} are unused.",
            _ => $"No {items} match the search.",
        };

    internal bool Matches(params string[] texts)
    {
        var term = Search.Trim();
        return term.Length == 0 || texts.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// One volume as the Volumes page shows it.
/// </summary>
/// <param name="ShownName">The name, or for an anonymous volume (64 hex characters) its first twelve; the full name goes in a tooltip.</param>
/// <param name="Size">Its size once disk use has been read and the engine could measure it; null for unknown.</param>
/// <param name="UsedBy">The names of the containers that mount it, from the container list, by name.</param>
public sealed record VolumeRow(
    string Name,
    string ShownName,
    bool IsAnonymous,
    string Driver,
    string Mountpoint,
    DateTimeOffset? Created,
    long? Size,
    IReadOnlyList<string> UsedBy,
    ItemAction? Pending) : ISelectableRow
{
    /// <inheritdoc/>
    public string Key => Name;

    /// <inheritdoc/>
    public bool IsPending => Pending is not null;

    /// <summary>A container, in any state, mounts the volume.</summary>
    public bool InUse => UsedBy.Count > 0;
}

/// <summary>A container that mounts a volume: where inside it, and whether it can write there.</summary>
public sealed record VolumeUser(string ContainerId, string ContainerName, string Destination, bool ReadWrite);

/// <summary>Turns the engine's volume list into the rows the Volumes page shows.</summary>
public static partial class VolumeRows
{
    private const string AnonymousLabel = "com.docker.volume.anonymous";
    private const int ShortNameLength = 12;

    /// <summary>Name, ascending.</summary>
    public static readonly ListSort<VolumeColumn> DefaultSort = new(VolumeColumn.Name, Descending: false);

    /// <summary>Every row of the state's volume list, in the engine's order.</summary>
    public static IReadOnlyList<VolumeRow> From(EngineState state)
    {
        var users = state.Containers
            .SelectMany(c => c.Mounts.Where(IsVolume).Select(m => (Volume: m.Name!, Container: ComposeGrouping.NameOf(c))))
            .GroupBy(u => u.Volume, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)[.. g.Select(u => u.Container).Distinct().Order(StringComparer.OrdinalIgnoreCase)],
                StringComparer.Ordinal);
        var sizes = state.DiskUsage.Usage?.Volumes.ToDictionary(v => v.Name, v => v.Size, StringComparer.Ordinal);

        return
        [
            .. state.Volumes.Items.Select(v =>
            {
                var anonymous = v.Labels.ContainsKey(AnonymousLabel) || AnonymousName().IsMatch(v.Name);
                return new VolumeRow(
                    v.Name,
                    anonymous && v.Name.Length > ShortNameLength ? v.Name[..ShortNameLength] : v.Name,
                    anonymous,
                    v.Driver,
                    v.Mountpoint,
                    v.CreatedAt,
                    sizes?.GetValueOrDefault(v.Name),
                    users.GetValueOrDefault(v.Name) ?? [],
                    state.Volumes.Pending.TryGetValue(v.Name, out var action) ? action : null);
            }),
        ];
    }

    /// <summary>The rows <paramref name="filter"/> keeps, in <paramref name="sort"/>'s order. An unknown size sorts below any known one.</summary>
    public static IReadOnlyList<VolumeRow> Shown(IEnumerable<VolumeRow> rows, UsageFilter filter, ListSort<VolumeColumn> sort) =>
        sort.Apply(rows.Where(r => filter.Matches(r.Name) && filter.Keeps(r.InUse)), Comparison, r => r.Name);

    /// <summary>The containers that mount the volume <paramref name="name"/>, by container name, for its detail page.</summary>
    public static IReadOnlyList<VolumeUser> Users(EngineState state, string name) =>
    [
        .. state.Containers
            .SelectMany(c => c.Mounts
                .Where(m => IsVolume(m) && m.Name == name)
                .Select(m => new VolumeUser(c.Id, ComposeGrouping.NameOf(c), m.Destination, m.ReadWrite)))
            .OrderBy(u => u.ContainerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Destination, StringComparer.Ordinal),
    ];

    /// <summary>
    /// The line above the volumes table: "1.2 GB in use of 1.5 GB · 13 volumes" (in use is the engine's total less what removing the
    /// unused volumes would free), or "Calculating… · 13 volumes" until the disk use has been read.
    /// </summary>
    public static string UsageLine(EngineState state)
    {
        var count = state.Volumes.Items.Length;
        var volumes = count == 1 ? "1 volume" : $"{count} volumes";
        if (state.DiskUsage.Usage is not { } usage)
            return $"Calculating… · {volumes}";

        var totals = usage.VolumeTotals;
        return $"{ByteFormat.Bytes(totals.TotalSize - totals.Reclaimable)} in use of {ByteFormat.Bytes(totals.TotalSize)} · {volumes}";
    }

    /// <summary>
    /// What is wrong with <paramref name="name"/> for a new volume: null when it is empty (nothing to say yet) or good; otherwise that it
    /// breaks the engine's rule for names (<c>[a-zA-Z0-9][a-zA-Z0-9_.-]*</c>), or that a listed volume already has it (the engine would
    /// hand back that volume rather than refuse).
    /// </summary>
    public static string? NameError(string name, EngineState state) =>
        name.Length == 0 ? null
        : !RunDraft.IsName(name) ? RunDraft.NameError
        : state.Volumes.Items.Any(v => v.Name == name) ? $"A volume named {name} already exists."
        : null;

    /// <summary>Whether Create can send <paramref name="name"/>: not empty, no <see cref="NameError"/>, connected, and not being created already.</summary>
    public static bool CanCreate(string name, EngineState state) =>
        name.Length > 0
        && NameError(name, state) is null
        && state.Status == ConnectionStatus.Connected
        && !state.Volumes.Pending.ContainsKey(name);

    /// <summary>Why the table is empty: no volumes at all, nothing matching the search, or nothing the filter keeps.</summary>
    public static string EmptyText(bool engineHasVolumes, UsageFilter filter) => filter.EmptyText("volumes", engineHasVolumes);

    private static bool IsVolume(Engine.Models.MountDetails mount) => mount.Type == "volume" && !string.IsNullOrEmpty(mount.Name);

    private static Comparison<VolumeRow> Comparison(VolumeColumn column) => column switch
    {
        VolumeColumn.Driver => (a, b) => SortText.Compare(a.Driver, b.Driver),
        VolumeColumn.Created => (a, b) => Nullable.Compare(a.Created, b.Created),
        VolumeColumn.Size => (a, b) => Nullable.Compare(a.Size, b.Size),
        _ => (a, b) => SortText.Compare(a.Name, b.Name),
    };

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex AnonymousName();
}

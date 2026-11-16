using System.Collections.Immutable;
using Berth.Session;
using Berth.State;

namespace Berth.Presentation;

/// <summary>The columns of the networks table that can be sorted by.</summary>
public enum NetworkColumn { Name, Driver, Scope, Containers, Created }

/// <summary>
/// One network as the Networks page shows it.
/// </summary>
/// <param name="Containers">The names of the containers attached to it, from the container list, by name.</param>
/// <param name="IsBuiltIn">One of the engine's own networks (bridge, host, none), which cannot be removed.</param>
public sealed record NetworkRow(
    string Id,
    string Name,
    string Driver,
    string Scope,
    DateTimeOffset? Created,
    IReadOnlyList<string> Containers,
    bool IsBuiltIn,
    ItemAction? Pending) : ISelectableRow
{
    /// <inheritdoc/>
    public string Key => Id;

    /// <inheritdoc/>
    public bool IsPending => Pending is not null;

    /// <summary>A container, in any state, is attached to the network.</summary>
    public bool InUse => Containers.Count > 0;
}

/// <summary>Turns the engine's network list into the rows the Networks page shows.</summary>
public static class NetworkRows
{
    /// <summary>The engine's own networks, which it refuses to remove.</summary>
    public static readonly ImmutableHashSet<string> BuiltInNames = ImmutableHashSet.Create(StringComparer.Ordinal, "bridge", "host", "none");

    /// <summary>Name, ascending.</summary>
    public static readonly ListSort<NetworkColumn> DefaultSort = new(NetworkColumn.Name, Descending: false);

    /// <summary>Every row of the state's network list, in the engine's order.</summary>
    public static IReadOnlyList<NetworkRow> From(EngineState state)
    {
        var attached = state.Containers
            .SelectMany(c => c.Networks.Select(n => (Network: n, Container: ComposeGrouping.NameOf(c))))
            .GroupBy(a => a.Network, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)[.. g.Select(a => a.Container).Distinct().Order(StringComparer.OrdinalIgnoreCase)],
                StringComparer.Ordinal);

        return
        [
            .. state.Networks.Items.Select(n => new NetworkRow(
                n.Id,
                n.Name,
                n.Driver,
                n.Scope,
                n.Created,
                attached.GetValueOrDefault(n.Name) ?? [],
                BuiltInNames.Contains(n.Name),
                state.Networks.Pending.TryGetValue(n.Id, out var action) ? action : null)),
        ];
    }

    /// <summary>The rows <paramref name="filter"/> keeps (searching name, driver and ID), in <paramref name="sort"/>'s order.</summary>
    public static IReadOnlyList<NetworkRow> Shown(IEnumerable<NetworkRow> rows, UsageFilter filter, ListSort<NetworkColumn> sort) =>
        sort.Apply(rows.Where(r => filter.Matches(r.Name, r.Driver, r.Id) && filter.Keeps(r.InUse)), Comparison, r => r.Name);

    /// <summary>Whether a row offers Delete and its checkbox is enabled: not built in, connected and no action in progress on it.</summary>
    public static bool CanRemove(NetworkRow row, bool connected) => !row.IsBuiltIn && RowSelection.CanAct(row, connected);

    /// <summary>The checked, shown rows a bulk delete acts on: never a built-in network.</summary>
    public static ImmutableArray<string> RemoveKeys(IEnumerable<NetworkRow> visibleRows, IReadOnlySet<string> checkedKeys, bool connected) =>
        RowSelection.Checked(visibleRows, checkedKeys, connected, r => !r.IsBuiltIn);

    /// <summary>Why the table is empty: no networks at all, nothing matching the search, or nothing the filter keeps.</summary>
    public static string EmptyText(bool engineHasNetworks, UsageFilter filter) => filter.EmptyText("networks", engineHasNetworks);

    private static Comparison<NetworkRow> Comparison(NetworkColumn column) => column switch
    {
        NetworkColumn.Driver => (a, b) => SortText.Compare(a.Driver, b.Driver),
        NetworkColumn.Scope => (a, b) => SortText.Compare(a.Scope, b.Scope),
        NetworkColumn.Containers => (a, b) => a.Containers.Count.CompareTo(b.Containers.Count),
        NetworkColumn.Created => (a, b) => Nullable.Compare(a.Created, b.Created),
        _ => (a, b) => SortText.Compare(a.Name, b.Name),
    };
}

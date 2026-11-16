using System.Collections.Immutable;

namespace Berth.Presentation;

/// <summary>A row of a list whose rows can be checked and acted on in bulk: containers, images, volumes, networks.</summary>
public interface ISelectableRow
{
    /// <summary>What identifies the row and what an action on it sends: a container or network ID, an image tag or ID, a volume name.</summary>
    string Key { get; }

    /// <summary>An action is in progress on the row, so it is sent no other.</summary>
    bool IsPending { get; }
}

/// <summary>
/// What every list does with checked rows. Only rows the filter shows count: a checked row the search or filter hides stays checked
/// but is neither counted nor acted on. A pending row is never sent another action, and nothing is sent while the engine is not
/// connected (the session would drop it). What a confirmed action sends is worked out again when the user confirms, from the rows
/// as they are then (<see cref="Confirmed"/>).
/// </summary>
public static class RowSelection
{
    /// <summary>How many names a confirmation lists before it sums up the rest.</summary>
    public const int NamedInText = 10;

    /// <summary>The checked rows among <paramref name="visibleRows"/>, pending ones included.</summary>
    public static int SelectedCount<TRow>(IEnumerable<TRow> visibleRows, IReadOnlySet<string> checkedKeys) where TRow : ISelectableRow =>
        visibleRows.Count(r => checkedKeys.Contains(r.Key));

    /// <summary>Whether a row's own buttons act: connected and no action in progress on it.</summary>
    public static bool CanAct(ISelectableRow row, bool connected) => connected && !row.IsPending;

    /// <summary>
    /// The keys of the rows among <paramref name="visibleRows"/> that are checked, can act and, when given, pass
    /// <paramref name="applies"/>, in the rows' order; none while not connected.
    /// </summary>
    public static ImmutableArray<string> Checked<TRow>(
        IEnumerable<TRow> visibleRows, IReadOnlySet<string> checkedKeys, bool connected, Func<TRow, bool>? applies = null)
        where TRow : ISelectableRow =>
        [.. visibleRows.Where(r => checkedKeys.Contains(r.Key) && CanAct(r, connected) && (applies?.Invoke(r) ?? true)).Select(r => r.Key)];

    /// <summary>
    /// The rows to act on when the user confirms an action on <paramref name="requestedKeys"/>: those still among <paramref name="rows"/>,
    /// not pending and, when given, passing <paramref name="applies"/>, in the requested order. None while not connected.
    /// </summary>
    public static IReadOnlyList<TRow> Confirmed<TRow>(
        IEnumerable<TRow> rows, IEnumerable<string> requestedKeys, bool connected, Func<TRow, bool>? applies = null)
        where TRow : ISelectableRow
    {
        if (!connected)
            return [];

        var byKey = new Dictionary<string, TRow>(StringComparer.Ordinal);
        foreach (var row in rows)
            byKey.TryAdd(row.Key, row);

        return
        [
            .. requestedKeys
                .Distinct(StringComparer.Ordinal)
                .Where(byKey.ContainsKey)
                .Select(key => byKey[key])
                .Where(r => !r.IsPending && (applies?.Invoke(r) ?? true)),
        ];
    }

    /// <summary>The names a confirmation lists, one per line: at most <see cref="NamedInText"/>, then "and {n} more".</summary>
    public static IReadOnlyList<string> NameLines(IReadOnlyList<string> names) =>
        names.Count <= NamedInText ? [.. names] : [.. names.Take(NamedInText), $"and {names.Count - NamedInText} more"];
}

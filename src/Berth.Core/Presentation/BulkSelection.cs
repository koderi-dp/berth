using System.Collections.Immutable;

namespace Berth.Presentation;

/// <summary>What a delete sends: the container ids and whether running ones are stopped first (the engine's force flag).</summary>
public sealed record Removal(ImmutableArray<string> Ids, bool Force);

/// <summary>
/// The Containers page's decisions about checked rows, the delete confirmation and the empty list. Only rows the filter shows
/// count: a checked row the search or "running only" hides stays checked but is neither counted nor acted on. A row with an
/// action in progress is never sent another one, and nothing is sent while the engine is not connected (the session would
/// drop it).
/// </summary>
public static class BulkSelection
{
    /// <summary>How many names the delete confirmation lists before it sums up the rest.</summary>
    public const int NamedInDeleteText = 10;

    /// <summary>The checked rows among <paramref name="visibleRows"/>, pending ones included.</summary>
    public static int SelectedCount(IEnumerable<ContainerRow> visibleRows, IReadOnlySet<string> checkedIds) =>
        visibleRows.Count(r => checkedIds.Contains(r.Id));

    /// <summary>Whether a row's own buttons act: connected and no action in progress on it.</summary>
    public static bool CanAct(ContainerRow row, bool connected) => connected && row.Pending is null;

    /// <summary>Whether a compose project's buttons act: connected and no action in progress on any of its rows.</summary>
    public static bool CanAct(IEnumerable<ContainerRow> rows, bool connected) => connected && rows.All(r => r.Pending is null);

    /// <summary>Checked, shown, stopped and not pending; none while not connected.</summary>
    public static ImmutableArray<string> StartIds(IEnumerable<ContainerRow> visibleRows, IReadOnlySet<string> checkedIds, bool connected) =>
        Pick(visibleRows, checkedIds, connected, r => r.State == RowState.Stopped);

    /// <summary>Checked, shown, not stopped (running, paused or restarting) and not pending; none while not connected.</summary>
    public static ImmutableArray<string> StopIds(IEnumerable<ContainerRow> visibleRows, IReadOnlySet<string> checkedIds, bool connected) =>
        Pick(visibleRows, checkedIds, connected, r => r.State != RowState.Stopped);

    /// <summary>Checked, shown and not pending; none while not connected.</summary>
    public static ImmutableArray<string> DeleteIds(IEnumerable<ContainerRow> visibleRows, IReadOnlySet<string> checkedIds, bool connected) =>
        Pick(visibleRows, checkedIds, connected, _ => true);

    /// <summary>
    /// What to remove when the user confirms a delete of <paramref name="requestedIds"/>: those still among <paramref name="rows"/>
    /// and not pending, in the requested order; forced when any of them is not stopped. Nothing while not connected.
    /// </summary>
    public static Removal Removal(IEnumerable<ContainerRow> rows, IEnumerable<string> requestedIds, bool connected)
    {
        if (!connected)
            return new Removal([], Force: false);

        var byId = rows.ToDictionary(r => r.Id);
        ContainerRow[] kept =
        [
            .. requestedIds
                .Select(id => byId.GetValueOrDefault(id))
                .OfType<ContainerRow>()
                .Where(r => r.Pending is null),
        ];
        return new Removal([.. kept.Select(r => r.Id)], kept.Any(r => r.State != RowState.Stopped));
    }

    /// <summary>
    /// The delete confirmation's text. One container: "{name} will be removed. …". Several: "{n} containers will be removed:",
    /// then the names one per line (at most <see cref="NamedInDeleteText"/>, then "and {m} more"), then what is lost.
    /// A container that is not stopped adds that it is stopped first.
    /// </summary>
    public static string DeleteText(IReadOnlyList<ContainerRow> rows)
    {
        var anyRunning = rows.Any(r => r.State != RowState.Stopped);
        if (rows.Count == 1)
        {
            var text = $"{rows[0].Name} will be removed. Its data that is not in a volume is lost.";
            return anyRunning ? text + " It is running and will be stopped first." : text;
        }

        var lines = new List<string> { $"{rows.Count} containers will be removed:" };
        lines.AddRange(rows.Take(NamedInDeleteText).Select(r => r.Name));
        if (rows.Count > NamedInDeleteText)
            lines.Add($"and {rows.Count - NamedInDeleteText} more");
        lines.Add(anyRunning
            ? "Their data that is not in a volume is lost. Those that are running will be stopped first."
            : "Their data that is not in a volume is lost.");
        return string.Join("\n", lines);
    }

    /// <summary>Why the list is empty: no containers at all, nothing matching the search, or nothing running.</summary>
    public static string EmptyText(bool engineHasContainers, ContainerFilter filter) =>
        !engineHasContainers ? "No containers on this engine."
        : filter.RunningOnly && string.IsNullOrWhiteSpace(filter.Search) ? "No containers are running."
        : "No containers match the search.";

    private static ImmutableArray<string> Pick(IEnumerable<ContainerRow> visibleRows, IReadOnlySet<string> checkedIds, bool connected, Func<ContainerRow, bool> applies) =>
        [.. visibleRows.Where(r => checkedIds.Contains(r.Id) && CanAct(r, connected) && applies(r)).Select(r => r.Id)];
}

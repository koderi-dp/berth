using Berth.Engine.Models;
using Berth.State;

namespace Berth.Presentation;

/// <summary>
/// How the containers table uses a <see cref="ContainerSort"/>. Rows inside a compose project sort by the same key, and a project
/// sorts among the other rows by its name (for <see cref="SortColumn.Name"/>) or else by the key of its first row after its rows are
/// sorted. Rows or groups whose keys are equal fall back to the name, ascending, in either direction.
/// </summary>
public static class ContainerSorting
{
    extension(ContainerSort sort)
    {
        /// <summary>
        /// The sort after a click on <paramref name="column"/>'s title: the same column reverses, another one sorts ascending, except
        /// CPU, which sorts the busiest first.
        /// </summary>
        public ContainerSort Toggle(SortColumn column) =>
            column == sort.Column ? sort with { Descending = !sort.Descending } : new ContainerSort(column, Descending: column == SortColumn.Cpu);

        /// <summary>What a screen reader says for a column title: "Name, sorted ascending", or "Image, not sorted".</summary>
        public string AccessibleName(SortColumn column) =>
            SortText.AccessibleName(ContainerSort.Title(column), column == sort.Column, sort.Descending);

        /// <summary>
        /// The sort the table shows: a CPU sort shows as <see cref="ContainerSort.Default"/> while the CPU column is not shown (a narrow
        /// window), so rows do not move on figures nobody sees; the saved sort stays CPU and comes back with the column.
        /// </summary>
        public ContainerSort Shown(bool cpuColumnShown) => sort.Column == SortColumn.Cpu && !cpuColumnShown ? ContainerSort.Default : sort;

        /// <summary>The groups and their rows in this order. <paramref name="stats"/> are the latest stats, which CPU sorts by.</summary>
        public IReadOnlyList<ContainerGroup> Apply(IReadOnlyList<ContainerGroup> groups, IReadOnlyDictionary<string, StatsSample>? stats = null)
        {
            var comparer = RowComparer(sort, stats);
            var sorted = groups.Select(g => g with { Rows = [.. Order(sort, g.Rows, r => r, r => r.Name, comparer)] });
            return [.. Order(sort, sorted, g => GroupKeyRow(sort, g), GroupName, comparer)];
        }
    }

    extension(ContainerSort)
    {
        /// <summary>A column's title in the table: its name, or "CPU (%)".</summary>
        public static string Title(SortColumn column) => column == SortColumn.Cpu ? "CPU (%)" : column.ToString();
    }

    /// <summary>Orders items by the key of a row that stands for them, then by a name, ascending, ignoring case first.</summary>
    private static IEnumerable<T> Order<T>(ContainerSort sort, IEnumerable<T> items, Func<T, ContainerRow> keyRow, Func<T, string> name, IComparer<ContainerRow> comparer)
    {
        var byKey = sort.Descending
            ? items.OrderByDescending(i => keyRow(i), comparer)
            : items.OrderBy(i => keyRow(i), comparer);
        return byKey
            .ThenBy(name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name, StringComparer.Ordinal);
    }

    /// <summary>A project's name is its key for <see cref="SortColumn.Name"/>: it stands in as a row with that name.</summary>
    private static ContainerRow GroupKeyRow(ContainerSort sort, ContainerGroup group) =>
        sort.Column == SortColumn.Name && group.Project is { } project ? group.Rows[0] with { Name = project } : group.Rows[0];

    private static string GroupName(ContainerGroup group) => group.Project ?? group.Rows[0].Name;

    /// <summary>
    /// The comparer of the sort's column. CPU compares the figure the cell shows (one decimal), so rows that read the same keep their
    /// name order from one batch of stats to the next; a row without a figure (not running, or no sample yet) is below 0.
    /// </summary>
    private static IComparer<ContainerRow> RowComparer(ContainerSort sort, IReadOnlyDictionary<string, StatsSample>? stats) => sort.Column switch
    {
        SortColumn.Cpu => Comparer<ContainerRow>.Create((a, b) => ShownCpu(a, stats).CompareTo(ShownCpu(b, stats))),
        SortColumn.Image => Comparer<ContainerRow>.Create((a, b) => SortText.Compare(a.Image, b.Image)),
        SortColumn.Status => Comparer<ContainerRow>.Create((a, b) => StatusRank(a.State).CompareTo(StatusRank(b.State))),
        SortColumn.Created => Comparer<ContainerRow>.Create((a, b) => a.Created.CompareTo(b.Created)),
        _ => Comparer<ContainerRow>.Create((a, b) => SortText.Compare(a.Name, b.Name)),
    };

    private static double ShownCpu(ContainerRow row, IReadOnlyDictionary<string, StatsSample>? stats) =>
        StatsSummary.CpuOf(row, stats) is { } cpu ? Math.Round(cpu, 1, MidpointRounding.AwayFromZero) : -1;

    /// <summary>Running first, then restarting, paused, stopped: the more alive, the earlier.</summary>
    private static int StatusRank(RowState state) => state switch
    {
        RowState.Running => 0,
        RowState.Restarting => 1,
        RowState.Paused => 2,
        _ => 3,
    };
}

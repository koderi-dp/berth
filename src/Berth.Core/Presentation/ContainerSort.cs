using Berth.Engine.Models;

namespace Berth.Presentation;

/// <summary>The columns of the containers table that can be sorted by.</summary>
public enum SortColumn { Name, Image, Status, Created, Cpu }

/// <summary>
/// How the containers table is sorted: by one column, ascending or descending. Rows inside a compose project sort by the same key,
/// and a project sorts among the other rows by its name (for <see cref="SortColumn.Name"/>) or else by the key of its first row
/// after its rows are sorted. Rows or groups whose keys are equal fall back to the name, ascending, in either direction.
/// </summary>
public sealed record ContainerSort(SortColumn Column, bool Descending)
{
    /// <summary>Name, ascending.</summary>
    public static readonly ContainerSort Default = new(SortColumn.Name, Descending: false);

    /// <summary>
    /// The sort after a click on <paramref name="column"/>'s title: the same column reverses, another one sorts ascending, except
    /// CPU, which sorts the busiest first.
    /// </summary>
    public ContainerSort Toggle(SortColumn column) =>
        column == Column ? this with { Descending = !Descending } : new ContainerSort(column, Descending: column == SortColumn.Cpu);

    /// <summary>What a screen reader says for a column title: "Name, sorted ascending", or "Image, not sorted".</summary>
    public string AccessibleName(SortColumn column) =>
        column != Column ? $"{Title(column)}, not sorted"
        : Descending ? $"{Title(column)}, sorted descending"
        : $"{Title(column)}, sorted ascending";

    /// <summary>
    /// The saved sort as read from settings: a column name (any case) and a direction. A missing or unknown column is
    /// <see cref="SortColumn.Name"/>, a missing direction is ascending.
    /// </summary>
    public static ContainerSort Read(string? column, bool? descending)
    {
        // Enum.TryParse also takes numbers ("7") and lists ("Name,Image"), which name no column: only a single name counts.
        var known = column is { Length: > 0 } && char.IsLetter(column[0]) && !column.Contains(',') && Enum.TryParse<SortColumn>(column, ignoreCase: true, out var parsed)
            ? parsed
            : Default.Column;
        return new ContainerSort(known, descending ?? Default.Descending);
    }

    /// <summary>
    /// The sort the table shows: a CPU sort shows as <see cref="Default"/> while the CPU column is not shown (a narrow window), so rows
    /// do not move on figures nobody sees; the saved sort stays CPU and comes back with the column.
    /// </summary>
    public ContainerSort Shown(bool cpuColumnShown) => Column == SortColumn.Cpu && !cpuColumnShown ? Default : this;

    /// <summary>A column's title in the table: its name, or "CPU (%)".</summary>
    public static string Title(SortColumn column) => column == SortColumn.Cpu ? "CPU (%)" : column.ToString();

    /// <summary>The groups and their rows in this order. <paramref name="stats"/> are the latest stats, which CPU sorts by.</summary>
    public IReadOnlyList<ContainerGroup> Apply(IReadOnlyList<ContainerGroup> groups, IReadOnlyDictionary<string, StatsSample>? stats = null)
    {
        var comparer = RowComparer(stats);
        var sorted = groups.Select(g => g with { Rows = [.. Order(g.Rows, r => r, r => r.Name, comparer)] });
        return [.. Order(sorted, GroupKeyRow, GroupName, comparer)];
    }

    /// <summary>Orders items by the key of a row that stands for them, then by a name, ascending, ignoring case first.</summary>
    private IEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, ContainerRow> keyRow, Func<T, string> name, IComparer<ContainerRow> comparer)
    {
        var byKey = Descending
            ? items.OrderByDescending(i => keyRow(i), comparer)
            : items.OrderBy(i => keyRow(i), comparer);
        return byKey
            .ThenBy(name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name, StringComparer.Ordinal);
    }

    /// <summary>A project's name is its key for <see cref="SortColumn.Name"/>: it stands in as a row with that name.</summary>
    private ContainerRow GroupKeyRow(ContainerGroup group) =>
        Column == SortColumn.Name && group.Project is { } project ? group.Rows[0] with { Name = project } : group.Rows[0];

    private static string GroupName(ContainerGroup group) => group.Project ?? group.Rows[0].Name;

    /// <summary>
    /// The comparer of <see cref="Column"/>. CPU compares the figure the cell shows (one decimal), so rows that read the same keep
    /// their name order from one batch of stats to the next; a row without a figure (not running, or no sample yet) is below 0.
    /// </summary>
    private IComparer<ContainerRow> RowComparer(IReadOnlyDictionary<string, StatsSample>? stats) => Column switch
    {
        SortColumn.Cpu => Comparer<ContainerRow>.Create((a, b) => ShownCpu(a, stats).CompareTo(ShownCpu(b, stats))),
        SortColumn.Image => Comparer<ContainerRow>.Create((a, b) => CompareText(a.Image, b.Image)),
        SortColumn.Status => Comparer<ContainerRow>.Create((a, b) => StatusRank(a.State).CompareTo(StatusRank(b.State))),
        SortColumn.Created => Comparer<ContainerRow>.Create((a, b) => a.Created.CompareTo(b.Created)),
        _ => Comparer<ContainerRow>.Create((a, b) => CompareText(a.Name, b.Name)),
    };

    private static double ShownCpu(ContainerRow row, IReadOnlyDictionary<string, StatsSample>? stats) =>
        StatsSummary.CpuOf(row, stats) is { } cpu ? Math.Round(cpu, 1, MidpointRounding.AwayFromZero) : -1;

    private static int CompareText(string a, string b)
    {
        var ignoringCase = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return ignoringCase != 0 ? ignoringCase : StringComparer.Ordinal.Compare(a, b);
    }

    /// <summary>Running first, then restarting, paused, stopped: the more alive, the earlier.</summary>
    private static int StatusRank(RowState state) => state switch
    {
        RowState.Running => 0,
        RowState.Restarting => 1,
        RowState.Paused => 2,
        _ => 3,
    };
}

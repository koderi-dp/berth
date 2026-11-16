namespace Berth.Presentation;

/// <summary>
/// How the images, volumes or networks table is sorted: by one column, ascending or descending. A column's title is its name. Rows
/// whose keys are equal fall back to their name, ascending, in either direction.
/// </summary>
public sealed record ListSort<TColumn>(TColumn Column, bool Descending) where TColumn : struct, Enum
{
    /// <summary>The sort after a click on <paramref name="column"/>'s title: the same column reverses, another one sorts ascending.</summary>
    public ListSort<TColumn> Toggle(TColumn column) =>
        EqualityComparer<TColumn>.Default.Equals(column, Column) ? this with { Descending = !Descending } : new ListSort<TColumn>(column, Descending: false);

    /// <summary>What a screen reader says for a column title: "Name, sorted ascending", or "Size, not sorted".</summary>
    public string AccessibleName(TColumn column) =>
        SortText.AccessibleName(column.ToString(), EqualityComparer<TColumn>.Default.Equals(column, Column), Descending);

    /// <summary>
    /// <paramref name="rows"/> in this order: by <paramref name="byColumn"/>'s comparison for the sorted column, then by
    /// <paramref name="name"/> ascending (ignoring case first).
    /// </summary>
    internal IReadOnlyList<TRow> Apply<TRow>(IEnumerable<TRow> rows, Func<TColumn, Comparison<TRow>> byColumn, Func<TRow, string> name)
    {
        var comparer = Comparer<TRow>.Create(byColumn(Column));
        var byKey = Descending ? rows.OrderByDescending(r => r, comparer) : rows.OrderBy(r => r, comparer);
        return [.. byKey.ThenBy(name, StringComparer.OrdinalIgnoreCase).ThenBy(name, StringComparer.Ordinal)];
    }
}

/// <summary>Text comparison and screen reader names shared by every sortable table.</summary>
internal static class SortText
{
    /// <summary>Ignoring case first, then by case, so the order is stable and "Nginx" comes before "nginx".</summary>
    public static int Compare(string a, string b)
    {
        var ignoringCase = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return ignoringCase != 0 ? ignoringCase : StringComparer.Ordinal.Compare(a, b);
    }

    public static string AccessibleName(string title, bool sorted, bool descending) =>
        !sorted ? $"{title}, not sorted"
        : descending ? $"{title}, sorted descending"
        : $"{title}, sorted ascending";
}

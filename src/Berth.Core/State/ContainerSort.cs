namespace Berth.State;

/// <summary>The columns of the containers table that can be sorted by.</summary>
public enum SortColumn { Name, Image, Status, Created, Cpu }

/// <summary>
/// How the containers table is sorted: by one column, ascending or descending. This is the choice the settings keep; how the table
/// orders its rows by it, and how a click changes it, is ContainerSorting in Presentation.
/// </summary>
public sealed record ContainerSort(SortColumn Column, bool Descending)
{
    /// <summary>Name, ascending.</summary>
    public static readonly ContainerSort Default = new(SortColumn.Name, Descending: false);

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
}

using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Session;

namespace Berth.Presentation;

/// <summary>Turns the engine's container list into the rows and groups the Containers page shows.</summary>
public static class ComposeGrouping
{
    public const string ProjectLabel = "com.docker.compose.project";

    private const int ShortIdLength = 12;

    /// <summary>
    /// Groups containers by compose project; a container without a project is a group of its own. Groups are ordered by
    /// project name, or by the container's name for a group without a project, and rows by name, all ignoring case.
    /// A group whose project matches the search keeps all its rows; other groups keep the rows that match.
    /// "Running only" applies to every row, and groups left with no rows are dropped. A group's state comes from all
    /// its containers, not only the rows the filter keeps.
    /// </summary>
    public static IReadOnlyList<ContainerGroup> Build(
        ImmutableArray<ContainerSummary> containers,
        ImmutableDictionary<string, ContainerAction> pending,
        ContainerFilter filter)
    {
        var groups = new List<(string SortKey, ContainerGroup Group)>();

        foreach (var project in containers.GroupBy(Project).Where(g => g.Key is not null))
        {
            groups.Add((project.Key!, Group(project.Key, [.. project.Select(c => Row(c, pending))], filter)));
        }

        foreach (var container in containers.Where(c => Project(c) is null))
        {
            var row = Row(container, pending);
            groups.Add((row.Name, Group(null, [row], filter)));
        }

        return
        [
            .. groups
                .Where(g => g.Group.Rows.Count > 0)
                .OrderBy(g => g.SortKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.SortKey, StringComparer.Ordinal)
                .Select(g => g.Group),
        ];
    }

    private static ContainerGroup Group(string? project, ContainerRow[] rows, ContainerFilter filter)
    {
        var projectMatches = project is not null && filter.MatchesProject(project);
        ContainerRow[] kept =
        [
            .. rows
                .Where(r => (projectMatches || filter.MatchesRow(r)) && filter.Keeps(r))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Id, StringComparer.Ordinal),
        ];
        return new ContainerGroup(project, GroupState(rows), kept);
    }

    /// <summary>Running if any row runs, else restarting if any restarts, else paused if any is paused, else stopped.</summary>
    private static RowState GroupState(ContainerRow[] rows) =>
        rows.Any(r => r.State == RowState.Running) ? RowState.Running
        : rows.Any(r => r.State == RowState.Restarting) ? RowState.Restarting
        : rows.Any(r => r.State == RowState.Paused) ? RowState.Paused
        : RowState.Stopped;

    /// <summary>
    /// What a container is called under its compose project's row: its name without the project in front ("shop-web-1" under
    /// "shop" is "web-1"), since the project is already on the row above. A name that does not start that way is left as it is.
    /// </summary>
    public static string NameInProject(string name, string project)
    {
        var hasPrefix = name.Length > project.Length + 1
            && name.StartsWith(project, StringComparison.OrdinalIgnoreCase)
            && name[project.Length] is '-' or '_';
        return hasPrefix ? name[(project.Length + 1)..] : name;
    }

    private static string? Project(ContainerSummary container) =>
        container.Labels.TryGetValue(ProjectLabel, out var project) && !string.IsNullOrWhiteSpace(project) ? project : null;

    private static ContainerRow Row(ContainerSummary container, ImmutableDictionary<string, ContainerAction> pending)
    {
        return new ContainerRow(
            container.Id,
            ShortId(container.Id),
            NameOf(container),
            container.Image,
            State(container.State),
            container.Status,
            PortFormat.Links(container.Ports),
            DateTimeOffset.FromUnixTimeSeconds(container.Created),
            pending.TryGetValue(container.Id, out var action) ? action : null);
    }

    /// <summary>A container's name without the engine's leading "/", or its short ID when it has none.</summary>
    internal static string NameOf(ContainerSummary container)
    {
        var name = container.Names.Count > 0 ? container.Names[0].TrimStart('/') : "";
        return name.Length > 0 ? name : ShortId(container.Id);
    }

    /// <summary>The first twelve characters of an ID, after any "sha256:" in front, as the docker CLI shows it.</summary>
    internal static string ShortId(string id)
    {
        var hex = id.StartsWith("sha256:", StringComparison.Ordinal) ? id["sha256:".Length..] : id;
        return hex.Length > ShortIdLength ? hex[..ShortIdLength] : hex;
    }

    private static RowState State(string state) => state switch
    {
        "running" => RowState.Running,
        "paused" => RowState.Paused,
        "restarting" => RowState.Restarting,
        _ => RowState.Stopped,
    };
}

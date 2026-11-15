namespace Berth.Presentation;

/// <summary>
/// The Containers page's search box and "running only" switch. The search matches a row's name, image or id,
/// or its compose project name, ignoring case; a blank search matches everything.
/// </summary>
public sealed record ContainerFilter(string Search, bool RunningOnly)
{
    private string Term => Search.Trim();

    internal bool MatchesProject(string? project) =>
        Term.Length == 0 || (project is not null && Contains(project));

    internal bool MatchesRow(ContainerRow row) =>
        Term.Length == 0 || Contains(row.Name) || Contains(row.Image) || Contains(row.Id);

    /// <summary>"Running only" keeps everything that is not stopped: running, paused and restarting rows.</summary>
    internal bool Keeps(ContainerRow row) => !RunningOnly || row.State != RowState.Stopped;

    private bool Contains(string text) => text.Contains(Term, StringComparison.OrdinalIgnoreCase);
}

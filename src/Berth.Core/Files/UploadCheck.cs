namespace Berth.Files;

/// <summary>An upload would replace entries the user has not said yes to; nothing was sent.</summary>
public sealed class ReplaceNeededException(IReadOnlyList<string> names) : Exception("The folder has entries with the same names.")
{
    public IReadOnlyList<string> Names { get; } = names;
}

/// <summary>
/// The replace question of an upload, decided from the container as it is just before the archive is sent (a stat of each name), never
/// from the listing on screen, which can be stale, capped or incomplete. Asked again on every attempt, so a name that appeared while
/// the user was reading the question is asked about too.
/// </summary>
public static class UploadCheck
{
    /// <summary>The names an upload of <paramref name="sources"/> puts at the top of its folder.</summary>
    public static IReadOnlyList<string> NamesOf(IReadOnlyList<string> sources) =>
        [.. sources.Select(s => Path.GetFileName(Path.TrimEndingDirectorySeparator(s)))];

    /// <summary>
    /// The names that are in the folder (<paramref name="existing"/>) and not among those the user agreed to replace, compared as Linux
    /// does (case and all).
    /// </summary>
    public static IReadOnlyList<string> Unconfirmed(IReadOnlyList<string> names, IReadOnlySet<string> existing, IReadOnlyList<string> confirmed) =>
        [.. names.Where(existing.Contains).Where(n => !confirmed.Contains(n, StringComparer.Ordinal))];
}

namespace Berth.Files;

/// <summary>Why the exec listing gave no listing: the engine refused the exec (with its status), the script exited, or its output did not parse.</summary>
public sealed record ExecListingFailure(int? EngineStatus, int? ExitCode)
{
    public static readonly ExecListingFailure Unreadable = new(null, null);

    public static ExecListingFailure Refused(int engineStatus) => new(engineStatus, null);

    public static ExecListingFailure Exited(int exitCode) => new(null, exitCode);
}

/// <summary>
/// Which way a folder is listed. The exec listing (<see cref="ExecListing"/>) is quick and reads only the folder, but needs a running
/// container with <c>sh</c>, <c>find</c>, <c>stat</c> and <c>readlink</c>; the archive listing (<see cref="ArchiveListing"/>) works on
/// any container but makes the engine stream the folder's content. Every failure of the exec listing falls back to the archive
/// listing, which also gives the engine's own message for a path that is missing or not a folder.
/// </summary>
public static class ListingChoice
{
    /// <summary>
    /// The exec listing in a container that runs (not paused, not restarting) and where it has not been found unavailable; the archive
    /// listing otherwise.
    /// </summary>
    public static ListingWay First(bool running, bool execUnavailable) => running && !execUnavailable ? ListingWay.Exec : ListingWay.Archive;

    /// <summary>
    /// Whether the exec listing is not tried again in this container after <paramref name="failure"/>: the engine refused the exec (no
    /// <c>sh</c>, or the container no longer runs), or the tools are missing (126, 127). A folder that cannot be entered (the next one may
    /// be), output that did not parse, and a lost connection keep it.
    /// </summary>
    public static bool ExecUnavailableAfter(ExecListingFailure failure) =>
        failure.EngineStatus is > 0 || failure.ExitCode is 126 or 127;
}

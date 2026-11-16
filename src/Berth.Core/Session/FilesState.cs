using Berth.Files;

namespace Berth.Session;

/// <summary>Where the Files tab's listing stands.</summary>
public enum FilesStatus { Idle, Loading, Loaded, Failed }

/// <summary>
/// The Files tab of a container's page: the folder shown and how its listing stands. Only <see cref="DetailReducer"/> produces new
/// values. Paths and names are the user's data: they live here, in memory, and are never logged.
/// </summary>
/// <param name="Path">The folder shown, or being read.</param>
/// <param name="Listing">The last listing read; kept while the same folder is read again.</param>
/// <param name="Error">Why the listing failed, as the engine said it (or "… is not a folder."), when <see cref="Status"/> is Failed.</param>
/// <param name="NotAFolder">The path is a file or something else that is not a folder.</param>
/// <param name="Generation">The generation of the latest listing asked for; older answers are ignored.</param>
/// <param name="Way">How the listing being read, or shown, is read.</param>
/// <param name="ExecUnavailable">The exec listing does not work in this container (no shell or tools); the archive listing is used.</param>
public sealed record FilesState(
    ContainerPath Path, FilesStatus Status, FileListing? Listing, string? Error, bool NotAFolder, int Generation, ListingWay? Way, bool ExecUnavailable)
{
    public static readonly FilesState Initial = new(ContainerPath.Root, FilesStatus.Idle, null, null, false, 0, null, false);
}

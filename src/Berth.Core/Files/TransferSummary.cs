namespace Berth.Files;

/// <summary>
/// What a download or an upload did: the files and folders written and the bytes of the files, and what it left out and why.
/// </summary>
/// <param name="Links">Symbolic and hard links skipped: never created or followed (on upload, links and junctions on the disk).</param>
/// <param name="Special">Devices, FIFOs and other special files skipped.</param>
/// <param name="Unsafe">Entries skipped because their path would leave the chosen folder (<c>..</c>, an absolute path, another top folder).</param>
/// <param name="Renamed">Names Windows cannot hold, saved under a safe name (see <see cref="WindowsNames"/>).</param>
/// <param name="Clashes">Entries skipped because another of the same name on Windows was already saved (Windows ignores case).</param>
/// <param name="Saved">Where the first item downloaded was saved, for "Show in folder"; null for an upload or when nothing was saved.</param>
public sealed record TransferSummary(
    int Files, int Folders, long Bytes, int Links, int Special, int Unsafe, int Renamed, int Clashes, string? Saved)
{
    public static readonly TransferSummary Empty = new(0, 0, 0, 0, 0, 0, 0, 0, null);

    /// <summary>Both summaries together; the first saved path is kept.</summary>
    public TransferSummary Add(TransferSummary other) => new(
        Files + other.Files,
        Folders + other.Folders,
        Bytes + other.Bytes,
        Links + other.Links,
        Special + other.Special,
        Unsafe + other.Unsafe,
        Renamed + other.Renamed,
        Clashes + other.Clashes,
        Saved ?? other.Saved);

    /// <summary>Whether anything was left out or renamed.</summary>
    public bool HasNotes => Links + Special + Unsafe + Renamed + Clashes > 0;
}

/// <summary>A download would replace what is already in the chosen folder and was not told it may.</summary>
public sealed class TransferConflictException(string message) : IOException(message);

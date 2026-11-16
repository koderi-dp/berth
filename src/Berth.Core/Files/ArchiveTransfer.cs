using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Files;

/// <summary>How a download and an upload run against the engine: the sizing first, then the archive streamed one way or the other.</summary>
public static class ArchiveTransfer
{
    /// <summary>How many of an upload's names are checked against the container at once.</summary>
    private const int StatsAtOnce = 4;

    /// <summary>
    /// Saves each of <paramref name="sources"/> into <paramref name="destination"/> (see <see cref="ArchiveExtractor"/>), one archive after
    /// another. Sources whose names meet on Windows (README and readme, a:b and a_b) keep the first; the others are not read and count
    /// as clashes. The total is told to <paramref name="sized"/> first when every source is a plain file (a folder's size is not known
    /// before it is read), and such a download does not start when it would leave the drive under <see cref="ArchiveExtractor.FreeSpaceFloor"/>.
    /// A source that fails stops the download; those saved before it stay.
    /// </summary>
    public static async Task<TransferSummary> DownloadAsync(
        IEngineApi api,
        string containerId,
        IReadOnlyList<ContainerPath> sources,
        string destination,
        bool replace,
        Action<long> progress,
        Action<long> sized,
        CancellationToken ct,
        Func<long>? freeSpace = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = sources.Where(s => names.Add(WindowsNames.Make(s.Name).Name)).ToList();
        var summary = TransferSummary.Empty with { Clashes = sources.Count - kept.Count };

        long total = 0;
        var known = true;
        foreach (var source in kept)
        {
            var stat = await api.StatPathAsync(containerId, source.Value, ct).ConfigureAwait(false);
            known &= !stat.IsDirectory && !stat.IsLink;
            total += stat.Size;
        }

        freeSpace ??= ArchiveExtractor.FreeSpaceOf(destination);
        if (known)
        {
            sized(total);
            if (freeSpace() - total < ArchiveExtractor.FreeSpaceFloor)
                throw new DiskSpaceException(ArchiveExtractor.FloorText(destination));
        }

        foreach (var source in kept)
        {
            await using var archive = await api.GetArchiveAsync(containerId, source.Value, ct).ConfigureAwait(false);
            summary = summary.Add(await ArchiveExtractor.ExtractAsync(archive.Content, destination, replace, progress, ct, freeSpace).ConfigureAwait(false));
        }

        return summary;
    }

    /// <summary>
    /// Puts <paramref name="sources"/> (local files or folders) into the container's folder <paramref name="destination"/> as one archive
    /// (see <see cref="ArchiveWriter"/>). Before anything is sent: the folder must be there; every local file must open (else
    /// <see cref="UploadSourceException"/>, "N files could not be read … Nothing was sent."); and each top name is looked up in the
    /// container (a stat, a few at once), and a name that is there and not in <paramref name="replace"/> throws
    /// <see cref="ReplaceNeededException"/> (<see cref="UploadCheck"/>). The total is told to <paramref name="sized"/>. A cancelled upload
    /// cannot be undone: what the engine unpacked before it stays in the container.
    /// </summary>
    public static async Task<TransferSummary> UploadAsync(
        IEngineApi api,
        string containerId,
        IReadOnlyList<string> sources,
        ContainerPath destination,
        IReadOnlyList<string> replace,
        Action<long> progress,
        Action<long> sized,
        CancellationToken ct)
    {
        await CheckFolderAsync(api, containerId, destination, ct).ConfigureAwait(false);
        var check = LocalFiles.Inspect(sources);
        if (check.Unreadable > 0)
            throw new UploadSourceException(LocalFiles.UnreadableText(check.Unreadable));

        var names = UploadCheck.NamesOf(sources);
        var existing = await ExistingAsync(api, containerId, destination, names, ct).ConfigureAwait(false);
        if (UploadCheck.Unconfirmed(names, existing, replace) is { Count: > 0 } asks)
            throw new ReplaceNeededException(asks);

        sized(check.Bytes);
        TransferSummary? summary = null;
        await api.PutArchiveAsync(
            containerId,
            destination.Value,
            async (stream, token) => summary = await ArchiveWriter.WriteAsync(stream, sources, progress, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);
        return summary ?? TransferSummary.Empty;
    }

    /// <summary>The names that are in the folder now: a stat of each (404: not there), <see cref="StatsAtOnce"/> at a time.</summary>
    private static async Task<IReadOnlySet<string>> ExistingAsync(
        IEngineApi api, string containerId, ContainerPath folder, IReadOnlyList<string> names, CancellationToken ct)
    {
        var there = new HashSet<string>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(StatsAtOnce);
        await Task.WhenAll(names.Distinct(StringComparer.Ordinal).Where(ContainerPath.IsPlainName).Select(async name =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await api.StatPathAsync(containerId, folder.Join(name).Value, ct).ConfigureAwait(false);
                lock (there)
                    there.Add(name);
            }
            catch (EngineException ex) when (ex.StatusCode == 404)
            {
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        return there;
    }
    /// <summary>
    /// Makes sure the upload's folder is there before any byte is sent: the engine refuses a missing folder at once and closes the
    /// connection while the tar is still being written, which reads as a dropped connection rather than its message (seen on the daily
    /// engine even with <c>Expect: 100-continue</c>). A missing folder throws the engine's own message (from the archive call, whose
    /// error answer has a body; the stat's has none); a path that is not a folder throws <see cref="NotAFolderException"/>.
    /// </summary>
    private static async Task CheckFolderAsync(IEngineApi api, string containerId, ContainerPath destination, CancellationToken ct)
    {
        PathStat stat;
        try
        {
            stat = await api.StatPathAsync(containerId, destination.Value, ct).ConfigureAwait(false);
        }
        catch (EngineException ex) when (ex.StatusCode == 404)
        {
            await using var _ = await api.GetArchiveAsync(containerId, destination.Value, ct).ConfigureAwait(false);
            throw;
        }

        if (!stat.IsDirectory && !stat.IsLink)
            throw new NotAFolderException(destination);
    }
}

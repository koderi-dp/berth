using Berth.Engine;

namespace Berth.Files;

/// <summary>How a download and an upload run against the engine: the sizing first, then the archive streamed one way or the other.</summary>
public static class ArchiveTransfer
{
    /// <summary>
    /// Saves each of <paramref name="sources"/> into <paramref name="destination"/> (see <see cref="ArchiveExtractor"/>), one archive after
    /// another. The total is told to <paramref name="sized"/> first when every source is a plain file (a folder's size is not known
    /// before it is read). A source that fails stops the download; those saved before it stay.
    /// </summary>
    public static async Task<TransferSummary> DownloadAsync(
        IEngineApi api,
        string containerId,
        IReadOnlyList<ContainerPath> sources,
        string destination,
        bool replace,
        Action<long> progress,
        Action<long> sized,
        CancellationToken ct)
    {
        long total = 0;
        var known = true;
        foreach (var source in sources)
        {
            var stat = await api.StatPathAsync(containerId, source.Value, ct).ConfigureAwait(false);
            known &= !stat.IsDirectory && !stat.IsLink;
            total += stat.Size;
        }

        if (known)
            sized(total);

        var summary = TransferSummary.Empty;
        foreach (var source in sources)
        {
            await using var archive = await api.GetArchiveAsync(containerId, source.Value, ct).ConfigureAwait(false);
            summary = summary.Add(await ArchiveExtractor.ExtractAsync(archive.Content, destination, replace, progress, ct).ConfigureAwait(false));
        }

        return summary;
    }

    /// <summary>
    /// Puts <paramref name="sources"/> (local files or folders) into the container's folder <paramref name="destination"/> as one archive
    /// (see <see cref="ArchiveWriter"/>). The total (every file under them, links and junctions left out) is told to
    /// <paramref name="sized"/> first. A cancelled upload cannot be undone: what the engine unpacked before it stays in the container.
    /// </summary>
    public static async Task<TransferSummary> UploadAsync(
        IEngineApi api,
        string containerId,
        IReadOnlyList<string> sources,
        ContainerPath destination,
        Action<long> progress,
        Action<long> sized,
        CancellationToken ct)
    {
        sized(sources.Sum(SizeOf));
        TransferSummary? summary = null;
        await api.PutArchiveAsync(
            containerId,
            destination.Value,
            async (stream, token) => summary = await ArchiveWriter.WriteAsync(stream, sources, progress, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);
        return summary ?? TransferSummary.Empty;
    }

    private static long SizeOf(string path)
    {
        if (!Directory.Exists(path))
            return File.Exists(path) ? new FileInfo(path).Length : 0;

        long size = 0;
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            size += item is FileInfo file ? file.Length : SizeOf(item.FullName);
        }

        return size;
    }
}

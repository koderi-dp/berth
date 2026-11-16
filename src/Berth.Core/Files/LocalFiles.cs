namespace Berth.Files;

/// <summary>A local file of an upload could not be read. Berth's own words, never taken for the engine's failure.</summary>
public sealed class UploadSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The local side of an upload: which entries are links, and whether every file can be read before anything is sent.
/// </summary>
public static class LocalFiles
{
    /// <summary>What a look at an upload's sources found: files that cannot be opened, the bytes of those that can, links left out.</summary>
    public sealed record Check(int Unreadable, long Bytes, int Links);

    /// <summary>
    /// Whether an entry is a link, which an upload skips: a reparse point with a link target, that is a symbolic link or a junction
    /// (a mount point). Other reparse points (OneDrive's cloud files, deduplicated files, app execution aliases) have none and are read
    /// as ordinary files and folders.
    /// </summary>
    public static bool IsLink(FileAttributes attributes, string? linkTarget) =>
        attributes.HasFlag(FileAttributes.ReparsePoint) && linkTarget is not null;

    public static bool IsLink(FileSystemInfo item) => IsLink(item.Attributes, item.Attributes.HasFlag(FileAttributes.ReparsePoint) ? item.LinkTarget : null);

    /// <summary>
    /// Opens (and closes) every file under <paramref name="sources"/> as the upload will, so a file in use, removed or not allowed
    /// is found before anything is sent; a folder that cannot be listed counts as one unreadable item.
    /// </summary>
    public static Check Inspect(IReadOnlyList<string> sources)
    {
        var unreadable = 0;
        long bytes = 0;
        var links = 0;

        void File(FileInfo file)
        {
            try
            {
                using var _ = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
                bytes += file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }
        }

        void Folder(DirectoryInfo folder)
        {
            try
            {
                foreach (var item in folder.EnumerateFileSystemInfos())
                {
                    if (IsLink(item))
                        links++;
                    else if (item is DirectoryInfo sub)
                        Folder(sub);
                    else if (item is FileInfo file)
                        File(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }
        }

        foreach (var source in sources)
        {
            if (Directory.Exists(source))
                Folder(new DirectoryInfo(source));
            else
                File(new FileInfo(source));
        }

        return new Check(unreadable, bytes, links);
    }

    /// <summary>"2 files could not be read (…). Nothing was sent."</summary>
    public static string UnreadableText(int count) =>
        $"{(count == 1 ? "1 file" : $"{count} files")} could not be read (in use by another program, removed, or not allowed). Nothing was sent.";

    /// <summary>A read that failed after <paramref name="sent"/> files had gone.</summary>
    public static string FailedWhileSendingText(int sent) =>
        $"A file could not be read while it was being sent. {(sent == 1 ? "1 file was" : $"{sent} files were")} sent before this; the upload cannot be undone.";
}

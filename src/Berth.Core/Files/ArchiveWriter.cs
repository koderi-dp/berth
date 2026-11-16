using System.Formats.Tar;
using System.Text;

namespace Berth.Files;

/// <summary>
/// Writes the tar of an upload: local files under their own names, and local folders as trees ("site/", "site/css/a.css"), straight
/// onto the output as they are read (nothing is held whole). Each entry is ustar when its name is ASCII of 100 bytes at most and its
/// size under 8 GiB, PAX otherwise (long or non-ASCII names, large sizes). Files get mode 0644 and folders 0755, owner 0, and their
/// last write time. Symbolic links and junctions inside a folder are skipped and counted, never followed (other reparse points,
/// such as OneDrive's cloud files, are ordinary files: <see cref="LocalFiles.IsLink(FileSystemInfo)"/>). A source that cannot be read throws
/// <see cref="UploadSourceException"/>.
/// </summary>
public static class ArchiveWriter
{
    private const UnixFileMode PlainFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode FolderMode = PlainFileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private const long UstarMaxSize = 8L * 1024 * 1024 * 1024 - 1;

    /// <summary>
    /// Writes <paramref name="sources"/> (files or folders) to <paramref name="output"/>, reporting each piece of content read. A local
    /// file or folder that cannot be opened or read throws <see cref="UploadSourceException"/> saying how many files were sent before
    /// it; what <paramref name="output"/> throws (the connection) is thrown as it is.
    /// </summary>
    public static async Task<TransferSummary> WriteAsync(Stream output, IReadOnlyList<string> sources, Action<long> progress, CancellationToken ct)
    {
        var counts = new Counts();
        await using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var source in sources)
        {
            if (Directory.Exists(source))
                await WriteFolderAsync(writer, new DirectoryInfo(source), Path.GetFileName(Path.TrimEndingDirectorySeparator(source)), progress, counts, ct)
                    .ConfigureAwait(false);
            else
                await WriteFileAsync(writer, new FileInfo(source), Path.GetFileName(source), progress, counts, ct).ConfigureAwait(false);
        }

        return new TransferSummary(counts.Files, counts.Folders, counts.Bytes, counts.Links, 0, 0, 0, 0, null);
    }

    private static async Task WriteFolderAsync(
        TarWriter writer, DirectoryInfo folder, string name, Action<long> progress, Counts counts, CancellationToken ct)
    {
        await writer.WriteEntryAsync(Entry(TarEntryType.Directory, name, 0, FolderMode, folder.LastWriteTimeUtc), ct).ConfigureAwait(false);
        counts.Folders++;
        FileSystemInfo[] items;
        try
        {
            items = [.. folder.EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UploadSourceException(LocalFiles.FailedWhileSendingText(counts.Files), ex);
        }

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var child = name + "/" + item.Name;
            if (LocalFiles.IsLink(item))
                counts.Links++;
            else if (item is DirectoryInfo sub)
                await WriteFolderAsync(writer, sub, child, progress, counts, ct).ConfigureAwait(false);
            else if (item is FileInfo file)
                await WriteFileAsync(writer, file, child, progress, counts, ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteFileAsync(TarWriter writer, FileInfo file, string name, Action<long> progress, Counts counts, CancellationToken ct)
    {
        FileStream content;
        try
        {
            content = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UploadSourceException(LocalFiles.FailedWhileSendingText(counts.Files), ex);
        }

        await using (content)
        {
            var entry = Entry(TarEntryType.RegularFile, name, content.Length, PlainFileMode, file.LastWriteTimeUtc);
            entry.DataStream = new Reporting(content, progress, counts);
            await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
            counts.Files++;
            counts.Bytes += content.Length;
        }
    }

    /// <summary>What has been written so far; the message of a failure says how many files went before it.</summary>
    private sealed class Counts
    {
        public int Files;
        public int Folders;
        public long Bytes;
        public int Links;
    }
    private static TarEntry Entry(TarEntryType type, string name, long size, UnixFileMode mode, DateTime modified)
    {
        TarEntry entry = Encoding.UTF8.GetByteCount(name) <= 100 && Ascii.IsValid(name) && size <= UstarMaxSize
            ? new UstarTarEntry(type, name)
            : new PaxTarEntry(type, name);
        entry.Mode = mode;
        entry.ModificationTime = new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc));
        entry.Uid = 0;
        entry.Gid = 0;
        return entry;
    }

    /// <summary>A file's content that reports each piece read; it seeks as the file does, so the writer knows its length.</summary>
    private sealed class Reporting(FileStream inner, Action<long> progress, Counts counts) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return Reported(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The local file's read, not the connection's write: Berth's own words.
                throw new UploadSourceException(LocalFiles.FailedWhileSendingText(counts.Files), ex);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Reported(int read)
        {
            if (read > 0)
                progress(read);
            return read;
        }
    }
}

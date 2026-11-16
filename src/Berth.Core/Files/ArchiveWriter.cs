using System.Formats.Tar;
using System.Text;

namespace Berth.Files;

/// <summary>
/// Writes the tar of an upload: local files under their own names, and local folders as trees ("site/", "site/css/a.css"), straight
/// onto the output as they are read (nothing is held whole). Each entry is ustar when its name is ASCII of 100 bytes at most and its
/// size under 8 GiB, PAX otherwise (long or non-ASCII names, large sizes). Files get mode 0644 and folders 0755, owner 0, and their
/// last write time. Links and junctions inside a folder are skipped and counted, never followed. A source that cannot be read throws
/// what the disk throws.
/// </summary>
public static class ArchiveWriter
{
    private const UnixFileMode PlainFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode FolderMode = PlainFileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private const long UstarMaxSize = 8L * 1024 * 1024 * 1024 - 1;

    /// <summary>Writes <paramref name="sources"/> (files or folders) to <paramref name="output"/>, reporting each piece of content read.</summary>
    public static async Task<TransferSummary> WriteAsync(Stream output, IReadOnlyList<string> sources, Action<long> progress, CancellationToken ct)
    {
        var summary = TransferSummary.Empty;
        await using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var source in sources)
        {
            if (Directory.Exists(source))
            {
                summary = await WriteFolderAsync(writer, new DirectoryInfo(source), Path.GetFileName(Path.TrimEndingDirectorySeparator(source)), progress, summary, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var file = new FileInfo(source);
                if (!file.Exists)
                    throw new FileNotFoundException("A file to upload is no longer there.");
                await WriteFileAsync(writer, file, file.Name, progress, ct).ConfigureAwait(false);
                summary = summary with { Files = summary.Files + 1, Bytes = summary.Bytes + file.Length };
            }
        }

        return summary;
    }

    private static async Task<TransferSummary> WriteFolderAsync(
        TarWriter writer, DirectoryInfo folder, string name, Action<long> progress, TransferSummary summary, CancellationToken ct)
    {
        await writer.WriteEntryAsync(Entry(TarEntryType.Directory, name, 0, FolderMode, folder.LastWriteTimeUtc), ct).ConfigureAwait(false);
        summary = summary with { Folders = summary.Folders + 1 };
        foreach (var item in folder.EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var child = name + "/" + item.Name;
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                summary = summary with { Links = summary.Links + 1 };
            else if (item is DirectoryInfo sub)
                summary = await WriteFolderAsync(writer, sub, child, progress, summary, ct).ConfigureAwait(false);
            else if (item is FileInfo file)
            {
                await WriteFileAsync(writer, file, child, progress, ct).ConfigureAwait(false);
                summary = summary with { Files = summary.Files + 1, Bytes = summary.Bytes + file.Length };
            }
        }

        return summary;
    }

    private static async Task WriteFileAsync(TarWriter writer, FileInfo file, string name, Action<long> progress, CancellationToken ct)
    {
        await using var content = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var entry = Entry(TarEntryType.RegularFile, name, content.Length, PlainFileMode, file.LastWriteTimeUtc);
        entry.DataStream = new Reporting(content, progress);
        await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
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
    private sealed class Reporting(FileStream inner, Action<long> progress) : Stream
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

        public override int Read(byte[] buffer, int offset, int count) => Reported(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Reported(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

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

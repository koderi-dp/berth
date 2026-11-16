using System.Formats.Tar;

namespace Berth.Files;

/// <summary>What one tar entry is.</summary>
public enum ArchiveEntryKind { File, Folder, SymbolicLink, HardLink, Device, Fifo, Other }

/// <summary>
/// One entry's header: its name as the archive spells it (a folder's usually ends with "/"), what it is, its content's size, its
/// permission bits, when it was modified, and a link's target.
/// </summary>
public sealed record ArchiveEntry(string Name, ArchiveEntryKind Kind, long Size, UnixFileMode Mode, DateTimeOffset Modified, string LinkTarget);

/// <summary>A tar stream Berth cannot read: damaged, cut off, or holding what Berth refuses (a sparse file).</summary>
public sealed class ArchiveFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads a tar stream one entry at a time, without seeking and without holding an entry's content: <see cref="NextAsync"/> gives the
/// next header, and <see cref="Data"/> the content of a regular file, readable until the next call, which skips what was not read.
/// Built on <c>System.Formats.Tar</c> (V7, ustar, GNU long names and long links, PAX extended headers for long paths and large sizes;
/// PAX global headers are skipped). A GNU sparse entry, or a PAX one (<c>GNU.sparse.*</c>), is refused: its content would read wrong.
/// The stream stays owned by the caller.
/// </summary>
public sealed class ArchiveReader(Stream archive) : IAsyncDisposable
{
    private const string SparseText = "The archive holds a sparse file, which Berth cannot read.";

    private readonly TarReader _reader = new(archive, leaveOpen: true);

    /// <summary>The current entry's content when it is a regular file; null otherwise.</summary>
    public Stream? Data { get; private set; }

    /// <summary>The next entry, or null at the end of the archive. A damaged or refused archive throws <see cref="ArchiveFormatException"/>.</summary>
    public async Task<ArchiveEntry?> NextAsync(CancellationToken ct)
    {
        Data = null;
        while (true)
        {
            TarEntry? entry;
            try
            {
                entry = await _reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false);
            }
            catch (NotSupportedException ex)
            {
                // System.Formats.Tar refuses a GNU sparse entry itself.
                throw new ArchiveFormatException(SparseText, ex);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException or ArgumentException)
            {
                throw new ArchiveFormatException("The archive could not be read: it is damaged or was cut off.", ex);
            }

            if (entry is null)
                return null;
            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
                continue;
            if (entry.EntryType == TarEntryType.SparseFile
                || entry is PaxTarEntry pax && pax.ExtendedAttributes.Keys.Any(k => k.StartsWith("GNU.sparse.", StringComparison.Ordinal)))
            {
                throw new ArchiveFormatException(SparseText);
            }

            var kind = KindOf(entry.EntryType);
            if (kind == ArchiveEntryKind.File && entry.DataStream is { } data)
                Data = new CutOffCheck(data);
            return new ArchiveEntry(
                entry.Name, kind, kind == ArchiveEntryKind.File ? entry.Length : 0, entry.Mode, entry.ModificationTime, entry.LinkName ?? "");
        }
    }

    public ValueTask DisposeAsync() => _reader.DisposeAsync();

    private static ArchiveEntryKind KindOf(TarEntryType type) => type switch
    {
        TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => ArchiveEntryKind.File,
        TarEntryType.Directory => ArchiveEntryKind.Folder,
        TarEntryType.SymbolicLink => ArchiveEntryKind.SymbolicLink,
        TarEntryType.HardLink => ArchiveEntryKind.HardLink,
        TarEntryType.CharacterDevice or TarEntryType.BlockDevice => ArchiveEntryKind.Device,
        TarEntryType.Fifo => ArchiveEntryKind.Fifo,
        _ => ArchiveEntryKind.Other,
    };

    /// <summary>An entry's content whose early end (the archive cut off inside it) throws <see cref="ArchiveFormatException"/>.</summary>
    private sealed class CutOffCheck(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw new ArchiveFormatException("The archive could not be read: it is damaged or was cut off.", ex);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

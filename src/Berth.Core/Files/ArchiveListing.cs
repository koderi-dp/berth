using System.Collections.Immutable;

namespace Berth.Files;

/// <summary>
/// Lists a folder from the engine's archive of it (a stopped container, one without the tools for <see cref="ExecListing"/>, or when
/// that failed): the headers are read and every entry's content skipped, and only the first level under the top entry is kept. The
/// engine streams the whole folder, content and all, so the read stops at a byte and a time budget and the listing then says it is
/// incomplete. Sizes of folders are not shown, as with the exec listing.
/// </summary>
public static class ArchiveListing
{
    /// <summary>How much of the archive is read, and for how long, before the listing stops.</summary>
    public sealed record Budget(long Bytes, TimeSpan Time)
    {
        /// <summary>256 MiB or 15 seconds.</summary>
        public static readonly Budget Default = new(256L * 1024 * 1024, TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// The first level of the archive of <paramref name="path"/>. An archive whose top entry is not a folder throws
    /// <see cref="NotAFolderException"/>; one that cannot be read, <see cref="ArchiveFormatException"/>. Cancelling
    /// <paramref name="ct"/> throws; running out of the budget does not.
    /// </summary>
    public static async Task<FileListing> ReadAsync(ContainerPath path, Stream archive, Budget budget, CancellationToken ct, TimeProvider? time = null)
    {
        using var deadline = new CancellationTokenSource(budget.Time, time ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var counted = new BudgetStream(archive, budget.Bytes);
        var entries = ImmutableArray.CreateBuilder<FileEntry>();
        var incomplete = false;
        var capped = false;
        await using var reader = new ArchiveReader(counted);
        try
        {
            var top = await reader.NextAsync(linked.Token).ConfigureAwait(false) ?? throw new ArchiveFormatException("The archive is empty.");
            if (top.Kind != ArchiveEntryKind.Folder)
                throw new NotAFolderException(path);

            var prefix = top.Name.TrimEnd('/') + "/";
            while (await reader.NextAsync(linked.Token).ConfigureAwait(false) is { } entry)
            {
                if (!entry.Name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var name = entry.Name[prefix.Length..].TrimEnd('/');
                if (name.Length == 0 || name.Contains('/'))
                    continue;
                if (entries.Count == FileListing.EntryLimit)
                {
                    capped = true;
                    break;
                }

                entries.Add(ToEntry(name, entry));
            }
        }
        catch (Exception ex) when (ex is BudgetSpentException || (ex is OperationCanceledException && deadline.IsCancellationRequested && !ct.IsCancellationRequested))
        {
            incomplete = true;
        }

        return new FileListing(path, entries.ToImmutable(), ListingWay.Archive, incomplete, capped);
    }

    private static FileEntry ToEntry(string name, ArchiveEntry entry)
    {
        var (kind, letter) = entry.Kind switch
        {
            ArchiveEntryKind.Folder => (FileKind.Folder, 'd'),
            ArchiveEntryKind.SymbolicLink => (FileKind.Link, 'l'),
            ArchiveEntryKind.File or ArchiveEntryKind.HardLink => (FileKind.File, '-'),
            ArchiveEntryKind.Device => (FileKind.Other, 'c'),
            ArchiveEntryKind.BlockDevice => (FileKind.Other, 'b'),
            ArchiveEntryKind.Fifo => (FileKind.Other, 'p'),
            _ => (FileKind.Other, '?'),
        };

        // A hard link's header carries no size: the size is its target's.
        long? size = entry.Kind == ArchiveEntryKind.File ? entry.Size : null;
        return new FileEntry(
            name,
            kind,
            size,
            entry.Modified,
            ModeText.Format(letter, (int)entry.Mode & 0xFFF),
            kind == FileKind.Link ? entry.LinkTarget : null);
    }

    private sealed class BudgetSpentException : Exception;

    /// <summary>The archive, read until <paramref name="budget"/> bytes have come; reading past that throws.</summary>
    private sealed class BudgetStream(Stream inner, long budget) : Stream
    {
        private long _read;

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
            if (_read >= budget)
                throw new BudgetSpentException();
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, budget - _read)], cancellationToken).ConfigureAwait(false);
            _read += read;
            return read;
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

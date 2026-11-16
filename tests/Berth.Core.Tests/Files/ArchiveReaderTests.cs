using System.Text;
using Berth.Files;

namespace Berth.Tests.Files;

/// <summary>
/// The streaming tar reader, on archives made by GNU tar (gnu, pax and ustar formats) and by the engine (Go's tar writer, PAX for long
/// names), each holding a folder, a file, a 150-character name, a symbolic link with a 150-character target, a hard link, a FIFO and a
/// non-ASCII name with spaces; and on archives built byte by byte.
/// </summary>
public sealed class ArchiveReaderTests
{
    private static readonly string Long = new('n', 150);

    private static Stream Fixture(string name) =>
        new NonSeekable(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Files", "Fixtures", name + ".tar")));

    private static async Task<List<(ArchiveEntry Entry, byte[]? Data)>> ReadAllAsync(Stream archive, bool readData = true)
    {
        var entries = new List<(ArchiveEntry, byte[]?)>();
        await using var reader = new ArchiveReader(archive);
        while (await reader.NextAsync(CancellationToken.None) is { } entry)
        {
            byte[]? data = null;
            if (readData && reader.Data is { } stream)
            {
                var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                data = copy.ToArray();
            }

            entries.Add((entry, data));
        }

        return entries;
    }

    [Fact]
    public async Task The_engines_archive_is_read_with_long_names_links_and_special_files()
    {
        var entries = await ReadAllAsync(Fixture("engine"));
        var byName = entries.ToDictionary(e => e.Entry.Name);

        Assert.Equal(ArchiveEntryKind.Folder, byName["src/"].Entry.Kind);
        Assert.Equal(ArchiveEntryKind.Folder, byName["src/dir/"].Entry.Kind);
        var hello = byName["src/dir/hello.txt"];
        Assert.Equal(ArchiveEntryKind.File, hello.Entry.Kind);
        Assert.Equal("hello\n"u8.ToArray(), hello.Data);
        Assert.Equal(6, hello.Entry.Size);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), hello.Entry.Modified);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, hello.Entry.Mode);
        Assert.Equal(Encoding.ASCII.GetBytes(Long + "-content"), byName[$"src/{Long}.txt"].Data);
        Assert.Equal(ArchiveEntryKind.SymbolicLink, byName["src/link"].Entry.Kind);
        Assert.Equal("/target/" + Long, byName["src/link"].Entry.LinkTarget);
        Assert.Equal(ArchiveEntryKind.HardLink, byName["src/hard"].Entry.Kind);
        Assert.Equal("src/dir/hello.txt", byName["src/hard"].Entry.LinkTarget);
        Assert.Equal(ArchiveEntryKind.Fifo, byName["src/fifo"].Entry.Kind);
        Assert.Equal("x"u8.ToArray(), byName["src/ünï a b"].Data);
    }

    [Theory]
    [InlineData("gnu")]
    [InlineData("pax")]
    public async Task Gnu_tars_long_names_and_long_links_are_read(string format)
    {
        var entries = await ReadAllAsync(Fixture(format));
        var byName = entries.ToDictionary(e => e.Entry.Name);

        Assert.Equal(Encoding.ASCII.GetBytes(Long + "-content"), byName[$"./{Long}.txt"].Data);
        Assert.Equal("/target/" + Long, byName["./link"].Entry.LinkTarget);
        Assert.Equal(ArchiveEntryKind.HardLink, byName["./dir/hello.txt"].Entry.Kind);
        Assert.Equal("./hard", byName["./dir/hello.txt"].Entry.LinkTarget);
        Assert.Equal("hello\n"u8.ToArray(), byName["./hard"].Data);
        Assert.Equal(ArchiveEntryKind.Fifo, byName["./fifo"].Entry.Kind);
        Assert.Equal("x"u8.ToArray(), byName["./ünï a b"].Data);
    }

    [Fact]
    public async Task A_ustar_archive_is_read()
    {
        var entries = await ReadAllAsync(Fixture("ustar"));

        Assert.Equal(["dir/", "dir/hello.txt", "ünï a b"], entries.Select(e => e.Entry.Name));
    }

    [Fact]
    public async Task Entries_whose_content_is_not_read_are_skipped()
    {
        var entries = await ReadAllAsync(Fixture("engine"), readData: false);

        Assert.Equal(8, entries.Count);
    }

    [Fact]
    public async Task A_pax_size_and_path_replace_the_headers_own()
    {
        var tar = new TarBytes()
            .Pax(("path", "a/" + Long), ("size", "5"))
            .Entry("short", '0', "12345"u8.ToArray(), sizeField: 0)
            .File("after", "z")
            .End();

        var entries = await ReadAllAsync(new MemoryStream(tar));

        Assert.Equal("a/" + Long, entries[0].Entry.Name);
        Assert.Equal(5, entries[0].Entry.Size);
        Assert.Equal("12345"u8.ToArray(), entries[0].Data);
        Assert.Equal("after", entries[1].Entry.Name);
    }

    [Fact]
    public async Task A_gnu_sparse_entry_is_refused_with_a_reason()
    {
        var tar = new TarBytes().Entry("sparse", 'S', new byte[512]).End();

        var error = await Assert.ThrowsAsync<ArchiveFormatException>(() => ReadAllAsync(new MemoryStream(tar)));

        Assert.Contains("sparse", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pax_sparse_entry_is_refused_with_a_reason()
    {
        var tar = new TarBytes().Pax(("GNU.sparse.major", "1"), ("GNU.sparse.minor", "0")).File("sparse", "x").End();

        var error = await Assert.ThrowsAsync<ArchiveFormatException>(() => ReadAllAsync(new MemoryStream(tar)));

        Assert.Contains("sparse", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_damaged_header_is_refused()
    {
        var tar = new TarBytes().File("a", "x").End();
        tar[10] ^= 0x55;

        await Assert.ThrowsAsync<ArchiveFormatException>(() => ReadAllAsync(new MemoryStream(tar)));
    }

    [Fact]
    public async Task An_archive_that_ends_inside_an_entry_is_refused()
    {
        var tar = new TarBytes().File("a", new string('x', 2000)).Unfinished()[..1024];

        await Assert.ThrowsAsync<ArchiveFormatException>(() => ReadAllAsync(new MemoryStream(tar)));
    }

    [Fact]
    public async Task Devices_are_told_apart_from_other_special_files()
    {
        var tar = new TarBytes().Entry("c", '3').Entry("b", '4').Entry("p", '6').End();

        var entries = await ReadAllAsync(new MemoryStream(tar));

        Assert.Equal([ArchiveEntryKind.Device, ArchiveEntryKind.Device, ArchiveEntryKind.Fifo], entries.Select(e => e.Entry.Kind));
        Assert.All(entries, e => Assert.Null(e.Data));
    }

    /// <summary>A stream that cannot seek, as the engine's answer cannot.</summary>
    internal sealed class NonSeekable(Stream inner) : Stream
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

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

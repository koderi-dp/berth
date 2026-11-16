using System.Text;
using Berth.Engine.Models;
using Berth.Files;

namespace Berth.Tests.Files;

/// <summary>
/// The two ways to list a folder: the exec listing's output (as busybox and GNU print it: the same bytes on alpine, ubuntu 24.04 and
/// busybox 1.36, checked against the real images), and the first level of an archive's headers; and the choice between them.
/// </summary>
public sealed class FileListingTests
{
    private static readonly ContainerPath Tmp = ContainerPath.Root.Join("tmp").Join("t");

    private static ExecOutput Output(string stat, string links = "", int? exitCode = 0, bool truncated = false) =>
        new(Encoding.UTF8.GetBytes(stat + "\0" + links), truncated, exitCode);

    [Fact]
    public void The_command_passes_the_path_as_an_argument_never_inside_the_script()
    {
        var path = ContainerPath.Root.Join("a'b; rm -rf $x").Join("-dash");

        var command = ExecListing.Command(path);

        Assert.Equal(["sh", "-c"], command.Take(2));
        Assert.Equal("/a'b; rm -rf $x/-dash", command[^1]);
        Assert.DoesNotContain("rm -rf", command[2], StringComparison.Ordinal);
        Assert.Contains("cd -- \"$1\"", command[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Awkward_names_are_read_whole()
    {
        // The bytes the real images print for a folder made with these names (spaces, a newline, unicode, a leading dash, links).
        var stat =
            "81a4/0/1794868607/./-dash/\n" +
            "a1ff/3/1794868607/./link2/\n" +
            "81a4/0/1794868607/./a b/\n" +
            "41ed/4096/1794868607/./sub dir/\n" +
            "81a4/0/1794868607/./ünï/\n" +
            "81a4/12/1794868607/./new\nline/\n" +
            "a1ff/10/1794868607/./link/\n";
        var links = "./link2\0x\ny\0./link\0/etc/hosts\0";

        var listing = ExecListing.Read(Tmp, Output(stat, links));

        Assert.NotNull(listing);
        Assert.Equal(["-dash", "link2", "a b", "sub dir", "ünï", "new\nline", "link"], listing.Entries.Select(e => e.Name));
        var byName = listing.Entries.ToDictionary(e => e.Name);
        Assert.Equal(FileKind.Folder, byName["sub dir"].Kind);
        Assert.Null(byName["sub dir"].Size);
        Assert.Equal(12, byName["new\nline"].Size);
        Assert.Equal(FileKind.Link, byName["link"].Kind);
        Assert.Equal("/etc/hosts", byName["link"].LinkTarget);
        Assert.Equal("x\ny", byName["link2"].LinkTarget);
        Assert.Equal("-rw-r--r--", byName["a b"].Mode);
        Assert.Equal("drwxr-xr-x", byName["sub dir"].Mode);
        Assert.Equal("lrwxrwxrwx", byName["link"].Mode);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794868607), byName["a b"].Modified);
        Assert.Equal(ListingWay.Exec, listing.Way);
        Assert.False(listing.Incomplete);
        Assert.False(listing.Capped);
    }

    [Fact]
    public void A_name_that_looks_like_a_record_stays_one_name()
    {
        // No name can hold "/", so a newline and a fake record inside a name cannot end it.
        var stat = "81a4/1/1/./x\n81a4\n/\n";

        var listing = ExecListing.Read(Tmp, Output(stat));

        Assert.Equal(["x\n81a4\n"], listing!.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Bytes_that_are_not_utf8_read_as_replacement_characters()
    {
        var bytes = "81a4/1/1/./"u8.ToArray().Concat(new byte[] { 0xff, (byte)'a' }).Concat("/\n\0"u8.ToArray()).ToArray();

        var listing = ExecListing.Read(Tmp, new ExecOutput(bytes, false, 0));

        Assert.Equal(["�a"], listing!.Entries.Select(e => e.Name));
    }

    [Theory]
    [InlineData("11a4", 'p')]
    [InlineData("21b6", 'c')]
    [InlineData("61b0", 'b')]
    [InlineData("c1ed", 's')]
    public void Special_files_are_other_with_their_mode_letter(string hex, char letter)
    {
        var listing = ExecListing.Read(Tmp, Output($"{hex}/0/1/./x/\n"));

        var entry = Assert.Single(listing!.Entries);
        Assert.Equal(FileKind.Other, entry.Kind);
        Assert.Equal(letter, entry.Mode[0]);
        Assert.Null(entry.Size);
    }

    [Theory]
    [InlineData("89ed", "-rwsr-xr-x")]
    [InlineData("89a4", "-rwSr--r--")]
    [InlineData("85a4", "-rw-r-Sr--")]
    [InlineData("43ff", "drwxrwxrwt")]
    [InlineData("8111", "-r---w---x")]
    public void Mode_text_shows_set_id_and_sticky_bits(string hex, string mode)
    {
        Assert.Equal(mode, Assert.Single(ExecListing.Read(Tmp, Output($"{hex}/0/1/./x/\n"))!.Entries).Mode);
    }

    [Fact]
    public void An_empty_folder_lists_nothing()
    {
        var listing = ExecListing.Read(Tmp, Output(""));

        Assert.Empty(listing!.Entries);
    }

    [Fact]
    public void Output_cut_at_the_limit_keeps_the_whole_records_and_says_so()
    {
        var output = new ExecOutput(Encoding.UTF8.GetBytes("81a4/1/1/./a/\n81a4/1/1/./bb"), Truncated: true, ExitCode: null);

        var listing = ExecListing.Read(Tmp, output);

        Assert.Equal(["a"], listing!.Entries.Select(e => e.Name));
        Assert.True(listing.Capped);
    }

    [Fact]
    public void More_entries_than_the_limit_are_cut_and_said()
    {
        var stat = string.Concat(Enumerable.Range(0, FileListing.EntryLimit + 5).Select(i => $"81a4/1/1/./f{i}/\n"));

        var listing = ExecListing.Read(Tmp, Output(stat));

        Assert.Equal(FileListing.EntryLimit, listing!.Entries.Length);
        Assert.True(listing.Capped);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void Entries_that_could_not_all_be_read_are_shown_as_incomplete(int exitCode)
    {
        var listing = ExecListing.Read(Tmp, Output("81a4/1/1/./a/\n", exitCode: exitCode));

        Assert.Equal(["a"], listing!.Entries.Select(e => e.Name));
        Assert.True(listing.Incomplete);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(127)]
    [InlineData(126)]
    [InlineData(1)]
    public void A_failed_run_is_no_listing(int exitCode)
    {
        Assert.Null(ExecListing.Read(Tmp, Output("", exitCode: exitCode)));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("81a4/1/1/x/\n")]
    [InlineData("81a4/one/1/./x/\n")]
    [InlineData("zz/1/1/./x/\n")]
    [InlineData("81a4/1/1/./x/y\n")]
    [InlineData("81a4/1/1/./x/")]
    public void Output_that_does_not_parse_is_no_listing(string stat)
    {
        Assert.Null(ExecListing.Read(Tmp, new ExecOutput(Encoding.UTF8.GetBytes(stat), false, 0)));
    }

    [Theory]
    [InlineData(127, true)]
    [InlineData(126, true)]
    [InlineData(3, false)]
    [InlineData(1, false)]
    public void Missing_tools_make_the_exec_listing_unavailable_but_a_folder_that_cannot_be_entered_does_not(int exitCode, bool unavailable)
    {
        Assert.Equal(unavailable, ListingChoice.ExecUnavailableAfter(ExecListingFailure.Exited(exitCode)));
    }

    [Fact]
    public void An_exec_the_engine_refuses_makes_it_unavailable_but_a_lost_connection_does_not()
    {
        Assert.True(ListingChoice.ExecUnavailableAfter(ExecListingFailure.Refused(409)));
        Assert.True(ListingChoice.ExecUnavailableAfter(ExecListingFailure.Refused(500)));
        Assert.False(ListingChoice.ExecUnavailableAfter(ExecListingFailure.Refused(0)));
        Assert.False(ListingChoice.ExecUnavailableAfter(ExecListingFailure.Unreadable));
    }

    [Theory]
    [InlineData(true, false, false, false, ListingWay.Exec)]
    [InlineData(false, false, false, false, ListingWay.Archive)]
    [InlineData(true, true, false, false, ListingWay.Archive)]
    [InlineData(true, false, true, false, ListingWay.Archive)]
    [InlineData(true, false, false, true, ListingWay.Archive)]
    public void The_exec_listing_is_used_only_in_a_running_container_where_it_works(
        bool running, bool paused, bool restarting, bool unavailable, ListingWay way)
    {
        Assert.Equal(way, ListingChoice.First(running && !paused && !restarting, unavailable));
    }

    private static MemoryStream Archive(TarBytes tar) => new(tar.End());

    [Fact]
    public async Task The_archive_listing_takes_the_first_level_under_the_top_entry()
    {
        var tar = new TarBytes()
            .Folder("etc")
            .Folder("etc/apk")
            .File("etc/apk/arch", "x86_64")
            .Folder("etc/apk/keys")
            .File("etc/hosts", "127.0.0.1")
            .Symlink("etc/mtab", "/proc/mounts")
            .Entry("etc/fifo", '6');

        var listing = await ArchiveListing.ReadAsync(ContainerPath.Root.Join("etc"), Archive(tar), ArchiveListing.Budget.Default, CancellationToken.None);

        Assert.Equal(["apk", "hosts", "mtab", "fifo"], listing.Entries.Select(e => e.Name));
        var byName = listing.Entries.ToDictionary(e => e.Name);
        Assert.Equal(FileKind.Folder, byName["apk"].Kind);
        Assert.Equal(9, byName["hosts"].Size);
        Assert.Equal("/proc/mounts", byName["mtab"].LinkTarget);
        Assert.Equal(FileKind.Other, byName["fifo"].Kind);
        Assert.Equal(ListingWay.Archive, listing.Way);
        Assert.False(listing.Incomplete);
    }

    [Fact]
    public async Task The_root_folders_archive_lists_its_first_level()
    {
        var tar = new TarBytes().Folder("/").File("/.dockerenv", "").Folder("/bin").Symlink("/bin/sh", "/bin/busybox").Folder("/etc");

        var listing = await ArchiveListing.ReadAsync(ContainerPath.Root, Archive(tar), ArchiveListing.Budget.Default, CancellationToken.None);

        Assert.Equal([".dockerenv", "bin", "etc"], listing.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task An_archive_of_a_file_is_not_a_folder()
    {
        var tar = new TarBytes().File("hosts", "x");

        await Assert.ThrowsAsync<NotAFolderException>(() =>
            ArchiveListing.ReadAsync(ContainerPath.Root.Join("etc").Join("hosts"), Archive(tar), ArchiveListing.Budget.Default, CancellationToken.None));
    }

    [Fact]
    public async Task A_listing_past_its_byte_budget_stops_and_says_it_is_incomplete()
    {
        var tar = new TarBytes().Folder("big").File("big/a", "a").Entry("big/huge", '0', new byte[64 * 1024]).File("big/z", "z");

        var listing = await ArchiveListing.ReadAsync(
            ContainerPath.Root.Join("big"), Archive(tar), new ArchiveListing.Budget(16 * 1024, TimeSpan.FromMinutes(1)), CancellationToken.None);

        Assert.Equal(["a", "huge"], listing.Entries.Select(e => e.Name));
        Assert.True(listing.Incomplete);
    }

    [Fact]
    public async Task A_listing_past_its_time_budget_stops_and_says_it_is_incomplete()
    {
        var stream = new ChunkStreamOf(new TarBytes().Folder("slow").File("slow/a", "a").Unfinished());

        var listing = await ArchiveListing.ReadAsync(
            ContainerPath.Root.Join("slow"), stream, new ArchiveListing.Budget(1 << 30, TimeSpan.FromMilliseconds(200)), CancellationToken.None);

        Assert.Equal(["a"], listing.Entries.Select(e => e.Name));
        Assert.True(listing.Incomplete);
    }

    [Fact]
    public async Task Cancelling_the_listing_is_not_taken_for_its_budget()
    {
        var stream = new ChunkStreamOf(new TarBytes().Folder("slow").Unfinished());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArchiveListing.ReadAsync(ContainerPath.Root.Join("slow"), stream, ArchiveListing.Budget.Default, cancel.Token));
    }

    /// <summary>A stream that gives these bytes and then waits, as an engine still walking a big folder does.</summary>
    private sealed class ChunkStreamOf(byte[] bytes) : Stream
    {
        private int _position;

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
            if (_position == bytes.Length)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

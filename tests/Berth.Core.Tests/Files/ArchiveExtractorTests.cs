using Berth.Files;

namespace Berth.Tests.Files;

/// <summary>
/// A download is an untrusted archive written to the user's folder: every entry stays under it, links are never created or followed,
/// special files are skipped, names Windows cannot hold are made safe, nothing is overwritten without a yes, and a download that does
/// not finish leaves nothing behind. Each test works in a folder of its own under the temp folder.
/// </summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "berth-tests", Guid.NewGuid().ToString("N"));
    private readonly string _destination;

    public ArchiveExtractorTests()
    {
        _destination = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(_destination);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Task<TransferSummary> ExtractAsync(byte[] tar, bool replace = false, CancellationToken ct = default, Action<long>? progress = null) =>
        ArchiveExtractor.ExtractAsync(new MemoryStream(tar), _destination, replace, progress ?? (_ => { }), ct);

    /// <summary>Every file and folder under the test's root, relative to it, with "/" separators.</summary>
    private string[] Everything() =>
        [.. Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    private string Read(string relative) => File.ReadAllText(Path.Combine(_destination, relative));

    [Fact]
    public void A_network_share_has_no_free_space_floor()
    {
        var freeSpace = ArchiveExtractor.FreeSpaceOf(@"\\berth-no-such-server\share\folder");

        Assert.Equal(long.MaxValue, freeSpace());
    }

    [Fact]
    public void A_local_folder_reports_its_drives_free_space()
    {
        var freeSpace = ArchiveExtractor.FreeSpaceOf(_destination);

        Assert.InRange(freeSpace(), 1, long.MaxValue - 1);
    }

    [Fact]
    public async Task A_file_is_saved_under_its_name_with_its_time()
    {
        var tar = new TarBytes().File("hello.txt", "hello").End();
        long reported = 0;

        var summary = await ExtractAsync(tar, progress: n => reported += n);

        Assert.Equal("hello", Read("hello.txt"));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(_destination, "hello.txt")));
        Assert.Equal(["downloads", "downloads/hello.txt"], Everything());
        Assert.Equal(1, summary.Files);
        Assert.Equal(5, summary.Bytes);
        Assert.Equal(5, reported);
        Assert.Equal(Path.Combine(_destination, "hello.txt"), summary.Saved);
    }

    [Fact]
    public async Task A_folder_is_saved_as_a_tree()
    {
        var tar = new TarBytes().Folder("src").Folder("src/dir").File("src/dir/a.txt", "a").File("src/b.txt", "bb").End();

        var summary = await ExtractAsync(tar);

        Assert.Equal(["downloads", "downloads/src", "downloads/src/b.txt", "downloads/src/dir", "downloads/src/dir/a.txt"], Everything());
        Assert.Equal((2, 2, 3L), (summary.Files, summary.Folders, summary.Bytes));
    }

    [Theory]
    [InlineData("src/../../evil.txt")]
    [InlineData("src/../evil.txt")]
    [InlineData("src/dir/../../../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("/src/evil.txt")]
    [InlineData("other/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("src\\..\\..\\evil.txt")]
    public async Task An_entry_that_would_leave_the_folder_is_skipped(string name)
    {
        var tar = new TarBytes().Folder("src").Entry(name, '0', "evil"u8.ToArray()).File("src/ok.txt", "ok").End();

        var summary = await ExtractAsync(tar);

        Assert.DoesNotContain(Everything(), p => p.EndsWith("evil.txt", StringComparison.Ordinal) && !p.StartsWith("downloads/src/", StringComparison.Ordinal));
        Assert.Equal("ok", Read("src/ok.txt"));
        Assert.Equal(1, summary.Unsafe);
    }

    [Fact]
    public async Task A_backslash_in_a_name_is_part_of_the_name_not_a_folder()
    {
        // A name with backslashes is renamed, never split into folders.
        var inside = new TarBytes().Folder("src").File("src/a\\..\\..\\b.txt", "x").End();

        await ExtractAsync(inside);

        Assert.Equal("x", Read("src/a_.._.._b.txt"));
        Assert.Equal(["downloads", "downloads/src", "downloads/src/a_.._.._b.txt"], Everything());
    }

    [Fact]
    public async Task Links_are_not_created_and_a_file_through_a_link_lands_in_a_real_folder()
    {
        var tar = new TarBytes()
            .Folder("src")
            .Symlink("src/link", _root)
            .File("src/link/evil.txt", "evil")
            .Entry("src/hard", '1', link: "src/link/evil.txt")
            .End();

        var summary = await ExtractAsync(tar);

        Assert.Equal(["downloads", "downloads/src", "downloads/src/link", "downloads/src/link/evil.txt"], Everything());
        var link = new DirectoryInfo(Path.Combine(_destination, "src", "link"));
        Assert.False(link.Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal(2, summary.Links);
    }

    [Fact]
    public async Task Devices_and_fifos_are_skipped()
    {
        var tar = new TarBytes().Folder("src").Entry("src/null", '3').Entry("src/disk", '4').Entry("src/pipe", '6').End();

        var summary = await ExtractAsync(tar);

        Assert.Equal(["downloads", "downloads/src"], Everything());
        Assert.Equal(3, summary.Special);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("com9.log.gz", "_com9.log.gz")]
    [InlineData("NUL ", "NUL_")]
    [InlineData("a:b", "a_b")]
    [InlineData("file.txt:stream", "file.txt_stream")]
    [InlineData("q?<>|\"*", "q______")]
    [InlineData("trail.", "trail_")]
    [InlineData("trail ", "trail_")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("new\nline", "new_line")]
    [InlineData("CONIN$", "_CONIN$")]
    [InlineData("conout$.txt", "_conout$.txt")]
    public async Task A_name_windows_cannot_hold_is_made_safe_and_counted(string name, string saved)
    {
        var tar = new TarBytes().Folder("src").File("src/" + name, "x").End();

        var summary = await ExtractAsync(tar);

        Assert.Equal("x", Read(Path.Combine("src", saved)));
        Assert.Equal(1, summary.Renamed);
    }

    [Fact]
    public async Task Ordinary_names_are_kept_as_they_are()
    {
        var tar = new TarBytes().Folder("src").File("src/ünï a b", "1").File("src/-dash", "2").File("src/console", "3").File("src/.hidden", "4").End();

        var summary = await ExtractAsync(tar);

        Assert.Equal(["downloads", "downloads/src", "downloads/src/-dash", "downloads/src/.hidden", "downloads/src/console", "downloads/src/ünï a b"], Everything());
        Assert.Equal(0, summary.Renamed);
    }

    [Fact]
    public async Task Names_that_meet_on_windows_are_saved_once_and_the_clash_is_counted()
    {
        var tar = new TarBytes().Folder("src").File("src/A", "upper").File("src/a", "lower").File("src/a:b", "1").File("src/a_b", "2").End();

        var summary = await ExtractAsync(tar);

        Assert.Equal("upper", Read("src/A"));
        Assert.Equal("1", Read("src/a_b"));
        Assert.Equal(2, summary.Clashes);
    }

    [Fact]
    public async Task An_unsafe_top_name_is_made_safe_too()
    {
        var summary = await ExtractAsync(new TarBytes().File("CON", "x").End());

        Assert.Equal("x", Read("_CON"));
        Assert.Equal(1, summary.Renamed);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("/")]
    [InlineData("a/../..")]
    public async Task An_archive_whose_top_entry_is_not_a_plain_name_is_refused(string name)
    {
        var tar = new TarBytes().Entry(name, '5').File(name.TrimEnd('/') + "/x", "x").End();

        await Assert.ThrowsAsync<ArchiveFormatException>(() => ExtractAsync(tar));

        Assert.Equal(["downloads"], Everything());
    }

    [Fact]
    public async Task An_existing_file_is_not_replaced_without_a_yes()
    {
        await File.WriteAllTextAsync(Path.Combine(_destination, "hello.txt"), "mine");

        var error = await Assert.ThrowsAsync<TransferConflictException>(() => ExtractAsync(new TarBytes().File("hello.txt", "theirs").End()));

        Assert.Equal("mine", Read("hello.txt"));
        Assert.Equal(["downloads", "downloads/hello.txt"], Everything());
        Assert.Contains("hello.txt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_file_is_replaced_after_a_yes()
    {
        await File.WriteAllTextAsync(Path.Combine(_destination, "hello.txt"), "mine");

        await ExtractAsync(new TarBytes().File("hello.txt", "theirs").End(), replace: true);

        Assert.Equal("theirs", Read("hello.txt"));
        Assert.Equal(["downloads", "downloads/hello.txt"], Everything());
    }

    [Fact]
    public async Task An_existing_folder_takes_the_new_files_after_a_yes_and_keeps_its_others()
    {
        Directory.CreateDirectory(Path.Combine(_destination, "src", "dir"));
        await File.WriteAllTextAsync(Path.Combine(_destination, "src", "keep.txt"), "kept");
        await File.WriteAllTextAsync(Path.Combine(_destination, "src", "dir", "a.txt"), "old");

        await ExtractAsync(new TarBytes().Folder("src").Folder("src/dir").File("src/dir/a.txt", "new").End(), replace: true);

        Assert.Equal("kept", Read("src/keep.txt"));
        Assert.Equal("new", Read("src/dir/a.txt"));
        Assert.Equal(["downloads", "downloads/src", "downloads/src/dir", "downloads/src/dir/a.txt", "downloads/src/keep.txt"], Everything());
    }

    [Fact]
    public async Task A_file_never_replaces_a_folder()
    {
        Directory.CreateDirectory(Path.Combine(_destination, "src"));

        await Assert.ThrowsAsync<TransferConflictException>(() => ExtractAsync(new TarBytes().File("src", "x").End(), replace: true));

        Assert.True(Directory.Exists(Path.Combine(_destination, "src")));
        Assert.Equal(["downloads", "downloads/src"], Everything());
    }

    [Fact]
    public async Task A_cancelled_download_leaves_no_partial_file()
    {
        var big = new byte[3 * 1024 * 1024];
        var tar = new TarBytes().Folder("src").File("src/small.txt", "s").Entry("src/big.bin", '0', big).End();
        using var cancel = new CancellationTokenSource();
        long seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExtractAsync(tar, ct: cancel.Token, progress: n =>
        {
            seen += n;
            if (seen > 1024 * 1024)
                cancel.Cancel();
        }));

        Assert.Equal(["downloads"], Everything());
    }

    [Fact]
    public async Task A_cancelled_download_of_a_file_that_is_there_leaves_it_as_it_was()
    {
        await File.WriteAllTextAsync(Path.Combine(_destination, "big.bin"), "mine");
        var tar = new TarBytes().Entry("big.bin", '0', new byte[2 * 1024 * 1024]).End();
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExtractAsync(tar, replace: true, ct: cancel.Token, progress: _ => cancel.Cancel()));

        Assert.Equal("mine", Read("big.bin"));
        Assert.Equal(["downloads", "downloads/big.bin"], Everything());
    }

    [Fact]
    public async Task A_damaged_archive_leaves_nothing_behind()
    {
        var tar = new TarBytes().Folder("src").File("src/a.txt", "a").File("src/b.txt", new string('b', 5000)).Unfinished()[..2048];

        await Assert.ThrowsAsync<ArchiveFormatException>(() => ExtractAsync(tar));

        Assert.Equal(["downloads"], Everything());
    }

    [Fact]
    public async Task A_sparse_file_refuses_the_download_and_leaves_nothing_behind()
    {
        var tar = new TarBytes().Folder("src").File("src/a.txt", "a").Entry("src/sparse", 'S', new byte[512]).End();

        await Assert.ThrowsAsync<ArchiveFormatException>(() => ExtractAsync(tar));

        Assert.Equal(["downloads"], Everything());
    }

    [Fact]
    public async Task A_link_or_device_at_the_top_saves_nothing_and_says_why()
    {
        var summary = await ExtractAsync(new TarBytes().Symlink("sh", "/bin/busybox").End());

        Assert.Equal(["downloads"], Everything());
        Assert.Equal((0, 1), (summary.Files, summary.Links));
        Assert.Null(summary.Saved);
    }

    [Fact]
    public async Task A_long_path_is_saved()
    {
        var deep = string.Join('/', Enumerable.Repeat(new string('d', 60), 5));
        var tar = new TarBytes().Pax(("path", "src/" + deep + "/f.txt")).File("x", "deep").End();
        var withRoot = new TarBytes().Folder("src").Concat(tar);

        await ExtractAsync(withRoot);

        Assert.Equal("deep", Read(Path.Combine("src", Path.Combine(deep.Split('/')), "f.txt")));
    }

    [Fact]
    public async Task Content_is_written_as_it_streams_not_held_whole()
    {
        var big = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        var tar = new TarBytes().Entry("big.bin", '0', big).End();
        var steps = 0;

        await ExtractAsync(tar, progress: _ => steps++);

        Assert.Equal(big, await File.ReadAllBytesAsync(Path.Combine(_destination, "big.bin")));
        Assert.True(steps > 8, "the content was written in pieces");
    }

    [Fact]
    public async Task A_folder_that_is_a_junction_is_not_merged_into()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   "cmd.exe", $"/c mklink /J \"{Path.Combine(_destination, "src")}\" \"{elsewhere}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            await mklink.WaitForExitAsync();

        await Assert.ThrowsAsync<TransferConflictException>(() => ExtractAsync(new TarBytes().Folder("src").File("src/a.txt", "a").End(), replace: true));

        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(_destination), p => Path.GetFileName(p).StartsWith(".berth-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_download_stops_when_the_drive_runs_low_and_leaves_nothing()
    {
        var tar = new TarBytes().Entry("big.bin", '0', new byte[4 * 1024 * 1024]).End();
        long free = 300L * 1024 * 1024;

        var error = await Assert.ThrowsAsync<DiskSpaceException>(() =>
            ArchiveExtractor.ExtractAsync(new MemoryStream(tar), _destination, false, n => free -= 60L * 1024 * 1024, CancellationToken.None, () => free));

        Assert.Equal(["downloads"], Everything());
        Assert.Contains("200 MB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_download_does_not_start_on_a_drive_that_is_already_low()
    {
        var tar = new TarBytes().File("a.txt", "a").End();

        await Assert.ThrowsAsync<DiskSpaceException>(() =>
            ArchiveExtractor.ExtractAsync(new MemoryStream(tar), _destination, false, _ => { }, CancellationToken.None, () => 100L * 1024 * 1024));

        Assert.Equal(["downloads"], Everything());
    }}

internal static class TarBytesExtensions
{
    /// <summary>The entries of <paramref name="first"/> followed by the whole of <paramref name="rest"/> (its end included).</summary>
    public static byte[] Concat(this TarBytes first, byte[] rest) => [.. first.Unfinished(), .. rest];
}


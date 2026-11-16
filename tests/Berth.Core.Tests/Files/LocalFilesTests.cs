using System.Diagnostics;
using Berth.Files;

namespace Berth.Tests.Files;

/// <summary>
/// The local side of an upload: which reparse points are links (only symbolic links and junctions; OneDrive's cloud files and
/// deduplicated files are ordinary files), and a file that cannot be read failing as Berth's own text, never as the engine's.
/// </summary>
public sealed class LocalFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "berth-tests", Guid.NewGuid().ToString("N"));

    public LocalFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Make(string relative, string text = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Theory]
    [InlineData(FileAttributes.Normal, null, false)]
    [InlineData(FileAttributes.ReparsePoint, "C:\\elsewhere", true)]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Directory, "C:\\elsewhere", true)]
    // A OneDrive placeholder, a deduplicated file or an app alias: a reparse point with no link target.
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Offline, null, false)]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Directory, null, false)]
    public void Only_a_reparse_point_with_a_link_target_is_a_link(FileAttributes attributes, string? target, bool link)
    {
        Assert.Equal(link, LocalFiles.IsLink(attributes, target));
    }

    [Fact]
    public async Task A_junction_inside_a_folder_is_skipped_as_a_link_and_its_files_are_not_sent()
    {
        Make(Path.Combine("site", "a.txt"));
        var outside = Path.Combine(_root, "outside");
        Make(Path.Combine("outside", "secret.txt"));
        var junction = Path.Combine(_root, "site", "j");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            await mklink.WaitForExitAsync();
        Assert.True(Directory.Exists(junction));

        var output = new MemoryStream();
        var summary = await ArchiveWriter.WriteAsync(output, [Path.Combine(_root, "site")], _ => { }, CancellationToken.None);

        Assert.Equal((1, 1), (summary.Files, summary.Links));
        Assert.Equal(new LocalFiles.Check(Unreadable: 0, Bytes: 1, Links: 1), LocalFiles.Inspect([Path.Combine(_root, "site")]));
    }

    [Fact]
    public void A_file_that_cannot_be_opened_is_counted_before_anything_is_sent()
    {
        var a = Make("a.txt", "aa");
        var b = Make("b.txt", "bbb");
        using var held = new FileStream(b, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var check = LocalFiles.Inspect([a, b, Path.Combine(_root, "gone.txt")]);

        Assert.Equal(new LocalFiles.Check(Unreadable: 2, Bytes: 2, Links: 0), check);
        Assert.Equal(
            "2 files could not be read (in use by another program, removed, or not allowed). Nothing was sent.",
            LocalFiles.UnreadableText(check.Unreadable));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_while_sending_fails_with_berths_text_and_what_was_sent()
    {
        var a = Make("a.txt", "aa");
        var b = Make("b.txt", "bbb");
        using var held = new FileStream(b, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var error = await Assert.ThrowsAsync<UploadSourceException>(() =>
            ArchiveWriter.WriteAsync(new MemoryStream(), [a, b], _ => { }, CancellationToken.None));

        Assert.Equal("A file could not be read while it was being sent. 1 file was sent before this; the upload cannot be undone.", error.Message);
        Assert.IsNotType<IOException>(error);
    }

    [Fact]
    public async Task A_file_removed_after_it_was_picked_fails_as_a_source_error()
    {
        var error = await Assert.ThrowsAsync<UploadSourceException>(() =>
            ArchiveWriter.WriteAsync(new MemoryStream(), [Path.Combine(_root, "gone.txt")], _ => { }, CancellationToken.None));

        Assert.Contains("0 files were sent before this", error.Message, StringComparison.Ordinal);
    }
}

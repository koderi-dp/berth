using System.Text;
using Berth.Files;

namespace Berth.Tests.Files;

/// <summary>The upload's tar: files and folder trees from the local disk, read back with <see cref="ArchiveReader"/>.</summary>
public sealed class ArchiveWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "berth-tests", Guid.NewGuid().ToString("N"));

    public ArchiveWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Make(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        return path;
    }

    private static async Task<List<(ArchiveEntry Entry, string? Text)>> ReadBackAsync(byte[] tar)
    {
        var entries = new List<(ArchiveEntry, string?)>();
        await using var reader = new ArchiveReader(new MemoryStream(tar));
        while (await reader.NextAsync(CancellationToken.None) is { } entry)
        {
            string? text = null;
            if (reader.Data is { } data)
                text = await new StreamReader(data, Encoding.UTF8).ReadToEndAsync();
            entries.Add((entry, text));
        }

        return entries;
    }

    private static async Task<(byte[] Tar, TransferSummary Summary, long Reported)> WriteAsync(params string[] sources)
    {
        var output = new MemoryStream();
        long reported = 0;
        var summary = await ArchiveWriter.WriteAsync(output, sources, n => reported += n, CancellationToken.None);
        return (output.ToArray(), summary, reported);
    }

    [Fact]
    public async Task Files_are_written_under_their_names_with_their_time_and_plain_modes()
    {
        var a = Make("a.txt", "alpha");
        var b = Make("b b.txt", "be");

        var (tar, summary, reported) = await WriteAsync(a, b);
        var entries = await ReadBackAsync(tar);

        Assert.Equal(["a.txt", "b b.txt"], entries.Select(e => e.Entry.Name));
        Assert.Equal(["alpha", "be"], entries.Select(e => e.Text));
        Assert.All(entries, e => Assert.Equal(ArchiveEntryKind.File, e.Entry.Kind));
        Assert.All(entries, e => Assert.Equal((UnixFileMode)0b110_100_100, e.Entry.Mode));
        Assert.All(entries, e => Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), e.Entry.Modified));
        Assert.Equal((2, 0, 7L), (summary.Files, summary.Folders, summary.Bytes));
        Assert.Equal(7, reported);
    }

    [Fact]
    public async Task A_folder_is_written_as_a_tree_with_forward_slashes()
    {
        Make(Path.Combine("site", "index.html"), "<p>");
        Make(Path.Combine("site", "css", "a.css"), "a{}");
        Directory.CreateDirectory(Path.Combine(_root, "site", "empty"));

        var (tar, summary, _) = await WriteAsync(Path.Combine(_root, "site"));
        var entries = await ReadBackAsync(tar);

        Assert.Equal(["site", "site/css", "site/css/a.css", "site/empty", "site/index.html"], entries.Select(e => e.Entry.Name.TrimEnd('/')).Order(StringComparer.Ordinal));
        Assert.All(entries.Where(e => e.Entry.Kind == ArchiveEntryKind.Folder), e => Assert.Equal((UnixFileMode)0b111_101_101, e.Entry.Mode));
        Assert.Equal((2, 3), (summary.Files, summary.Folders));
    }

    [Fact]
    public async Task Long_and_non_ascii_names_are_written_whole()
    {
        var name = new string('l', 120) + "-ünï.txt";
        var path = Make(name, "x");

        var (tar, _, _) = await WriteAsync(path);
        var entries = await ReadBackAsync(tar);

        Assert.Equal(name, Assert.Single(entries).Entry.Name);
    }

    [Fact]
    public async Task A_link_inside_a_folder_is_skipped_and_counted()
    {
        Make(Path.Combine("site", "a.txt"), "a");
        var outside = Make("outside.txt", "secret");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_root, "site", "link.txt"), outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Creating a link needs a privilege this machine does not give: nothing to check.
            return;
        }

        var (tar, summary, _) = await WriteAsync(Path.Combine(_root, "site"));
        var entries = await ReadBackAsync(tar);

        Assert.Equal(["site", "site/a.txt"], entries.Select(e => e.Entry.Name.TrimEnd('/')));
        Assert.Equal(1, summary.Links);
    }

    [Fact]
    public async Task A_missing_source_fails_as_it_is()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => WriteAsync(Path.Combine(_root, "gone.txt")));
    }
}

using Berth.Engine.Models;
using Berth.Files;
using Berth.Tests.Session;

namespace Berth.Tests.Files;

/// <summary>A download of several items against the stand-in engine: items whose names meet on Windows.</summary>
public sealed class ArchiveTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "berth-tests", Guid.NewGuid().ToString("N"));

    public ArchiveTransferTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_selected_items_with_the_same_windows_name_save_the_first_and_count_the_other(bool replace)
    {
        var api = new FakeEngineApi();
        api.PathStats["/x/README"] = new PathStat("README", 5, 0x1a4, null, "");
        api.PathStats["/y/readme"] = new PathStat("readme", 5, 0x1a4, null, "");
        api.Archives["/x/README"] = () => new MemoryStream(new TarBytes().File("README", "first").End());
        api.Archives["/y/readme"] = () => new MemoryStream(new TarBytes().File("readme", "other").End());
        ContainerPath[] sources = [ContainerPath.Root.Join("x").Join("README"), ContainerPath.Root.Join("y").Join("readme")];

        var summary = await ArchiveTransfer.DownloadAsync(api, "c", sources, _root, replace, _ => { }, _ => { }, CancellationToken.None);

        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(_root, "README")));
        Assert.Equal((1, 1), (summary.Files, summary.Clashes));
        Assert.DoesNotContain("GetArchive /y/readme", api.Calls);
    }
}

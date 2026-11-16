using System.Text;
using Berth.Engine;
using Berth.Engine.Models;
using Berth.Files;
using Berth.Session;
using Berth.Tests.Files;

namespace Berth.Tests.Session;

/// <summary>The Files tab's listing as the detail session runs it: exec first, the archive when that fails.</summary>
public sealed partial class ContainerDetailSessionTests
{
    private static readonly ContainerPath EtcPath = ContainerPath.Root.Join("etc");

    private static readonly PathStat FolderStat = new("etc", 4096, PathStat.DirectoryBit | 0x1ed, null, "");

    private static Func<Stream> EtcArchive() => () => new MemoryStream(new TarBytes().Folder("etc").File("etc/hosts", "127.0.0.1").End());

    [Fact]
    public async Task A_running_containers_folder_is_listed_through_exec()
    {
        await using var h = new Harness();
        await h.OpenAsync();
        h.Api.ExecOutput = new ExecOutput(Encoding.UTF8.GetBytes("81a4/9/1/./hosts/\n\0"), false, 0);

        h.Session.BrowseFiles(EtcPath);
        var state = await h.WaitFor(s => s.Files.Status == FilesStatus.Loaded);

        Assert.Equal(["hosts"], state.Files.Listing!.Entries.Select(e => e.Name));
        Assert.Equal(ListingWay.Exec, state.Files.Way);
        var (container, command) = Assert.Single(h.Api.OutputExecs);
        Assert.Equal(("c", "/etc"), (container, command[^1]));
        Assert.DoesNotContain(h.Api.Calls, c => c.StartsWith("GetArchive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_exec_falls_back_to_the_archive_and_is_not_tried_again()
    {
        await using var h = new Harness();
        await h.OpenAsync();
        h.Api.Fail("RunExecForOutput", new EngineException(409, "container is paused"));
        h.Api.PathStats["/etc"] = FolderStat;
        h.Api.Archives["/etc"] = EtcArchive();

        h.Session.BrowseFiles(EtcPath);
        var state = await h.WaitFor(s => s.Files.Status == FilesStatus.Loaded);

        Assert.Equal(["hosts"], state.Files.Listing!.Entries.Select(e => e.Name));
        Assert.Equal(ListingWay.Archive, state.Files.Way);
        Assert.True(state.Files.ExecUnavailable);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task A_path_that_is_a_file_is_not_a_folder()
    {
        await using var h = new Harness();
        await h.OpenAsync();
        h.Api.ExecOutput = new ExecOutput([], false, ExecListing.CannotEnter);
        h.Api.PathStats["/etc/hosts"] = new PathStat("hosts", 9, 0x1a4, null, "");
        h.Api.Archives["/etc/hosts"] = () => new MemoryStream(new TarBytes().File("hosts", "x").End());

        h.Session.BrowseFiles(EtcPath.Join("hosts"));
        var state = await h.WaitFor(s => s.Files.Status == FilesStatus.Failed);

        Assert.True(state.Files.NotAFolder);
        Assert.Equal("/etc/hosts is not a folder.", state.Files.Error);
    }

    [Fact]
    public async Task A_missing_path_shows_the_engines_message()
    {
        await using var h = new Harness();
        await h.OpenAsync();
        h.Api.ExecOutput = new ExecOutput([], false, ExecListing.CannotEnter);

        h.Session.BrowseFiles(ContainerPath.Root.Join("nope"));
        var state = await h.WaitFor(s => s.Files.Status == FilesStatus.Failed);

        Assert.Equal("Could not find the file /nope in container c", state.Files.Error);
        Assert.False(state.Files.NotAFolder);
    }

    [Fact]
    public async Task A_link_to_a_folder_is_listed_through_the_archive_of_the_folder_it_leads_to()
    {
        await using var h = new Harness();
        await h.OpenAsync();
        h.Api.ExecOutput = new ExecOutput([], false, 1);
        h.Api.PathStats["/lib64"] = new PathStat("lib64", 4, PathStat.SymbolicLinkBit | 0x1ff, null, "/lib");
        h.Api.Archives["/lib64"] = () => new MemoryStream(new TarBytes().Symlink("lib64", "/lib").End());
        h.Api.Archives["/lib64/."] = () => new MemoryStream(new TarBytes().Folder(".").File("./libc.so", "x").End());

        h.Session.BrowseFiles(ContainerPath.Root.Join("lib64"));
        var state = await h.WaitFor(s => s.Files.Status == FilesStatus.Loaded);

        Assert.Equal(["libc.so"], state.Files.Listing!.Entries.Select(e => e.Name));
    }
}

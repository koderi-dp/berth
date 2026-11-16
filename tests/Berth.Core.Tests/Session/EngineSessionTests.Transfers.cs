using Berth.Engine.Models;
using Berth.Files;
using Berth.Session;
using Berth.Tests.Engine;
using Berth.Tests.Files;

namespace Berth.Tests.Session;

/// <summary>The Files tab's downloads and uploads as the session runs them, against the stand-in engine and a temp folder.</summary>
public sealed partial class EngineSessionTests
{
    private static string TransferFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "berth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public async Task A_download_saves_the_archive_and_ends_with_its_summary()
    {
        var folder = TransferFolder();
        try
        {
            var api = new FakeEngineApi();
            api.PathStats["/etc/hosts"] = new PathStat("hosts", 9, 0x1a4, null, "");
            api.Archives["/etc/hosts"] = () => new MemoryStream(new TarBytes().File("hosts", "127.0.0.1").End());
            await using var h = Harness.With(api);
            await h.ConnectAsync();

            h.Session.Dispatch(new TransferRequested(new DownloadRequest("a", [ContainerPath.Root.Join("etc").Join("hosts")], folder, Replace: false)));
            var state = await h.WaitFor(s => s.Transfers.Values.Any(t => t.Status == TransferStatus.Done));

            var transfer = Assert.Single(state.Transfers.Values);
            Assert.Equal((1, 9L), (transfer.Summary!.Files, transfer.Summary.Bytes));
            Assert.Equal(Path.Combine(folder, "hosts"), transfer.Summary.Saved);
            Assert.Equal("127.0.0.1", await File.ReadAllTextAsync(Path.Combine(folder, "hosts")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_running_downloads_progress_reaches_the_state_in_batches()
    {
        var folder = TransferFolder();
        try
        {
            var api = new FakeEngineApi();
            api.PathStats["/big"] = new PathStat("big", 4096, 0x1a4, null, "");
            var body = new ChunkStream();
            api.Archives["/big"] = () => body;
            await using var h = Harness.With(api);
            await h.ConnectAsync();
            var tar = new TarBytes().Entry("big", '0', new byte[4096]).End();

            h.Session.Dispatch(new TransferRequested(new DownloadRequest("a", [ContainerPath.Root.Join("big")], folder, Replace: false)));
            body.Write(tar[..2048]);
            var running = await TickUntil(h, s => s.Transfers.Values.Any(t => t is { Done: > 0, Total: 4096 }));
            Assert.Equal(TransferStatus.Running, Assert.Single(running.Transfers.Values).Status);

            body.Write(tar[2048..]).End();
            await h.WaitFor(s => s.Transfers.Values.Any(t => t.Status == TransferStatus.Done));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_cancelled_download_leaves_nothing_and_sends_nothing_more()
    {
        var folder = TransferFolder();
        try
        {
            var api = new FakeEngineApi();
            api.PathStats["/big"] = new PathStat("big", 1 << 20, 0x1a4, null, "");
            var body = new ChunkStream();
            api.Archives["/big"] = () => body;
            await using var h = Harness.With(api);
            await h.ConnectAsync();
            var tar = new TarBytes().Entry("big", '0', new byte[1 << 20]).End();

            h.Session.Dispatch(new TransferRequested(new DownloadRequest("a", [ContainerPath.Root.Join("big")], folder, Replace: false)));
            body.Write(tar[..(256 * 1024)]);
            var running = await TickUntil(h, s => s.Transfers.Values.Any(t => t.Done > 0));
            var id = Assert.Single(running.Transfers.Keys);

            h.Session.Dispatch(new TransferCancelRequested(id));
            var cancelled = await h.WaitFor(s => s.Transfers[id].Status == TransferStatus.Cancelled);
            await WaitUntil(() => Directory.GetFileSystemEntries(folder).Length == 0);

            Assert.Equal(TransferStatus.Cancelled, cancelled.Transfers[id].Status);
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_download_of_a_missing_path_fails_with_the_engines_message()
    {
        var folder = TransferFolder();
        try
        {
            await using var h = Harness.With(new FakeEngineApi());
            await h.ConnectAsync();

            h.Session.Dispatch(new TransferRequested(new DownloadRequest("a", [ContainerPath.Root.Join("nope")], folder, Replace: false)));
            var state = await h.WaitFor(s => s.Transfers.Values.Any(t => t.Status == TransferStatus.Failed));

            Assert.Equal("Could not find the file /nope in container a", Assert.Single(state.Transfers.Values).Error);
            Assert.Equal(ConnectionStatus.Connected, state.Status);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task An_upload_puts_one_archive_of_the_files_into_the_folder()
    {
        var folder = TransferFolder();
        try
        {
            var file = Path.Combine(folder, "notes.txt");
            await File.WriteAllTextAsync(file, "hello");
            var api = new FakeEngineApi();
            api.PathStats["/tmp/berth-check"] = new PathStat("berth-check", 4096, PathStat.DirectoryBit | 0x1ed, null, "");
            await using var h = Harness.With(api);
            await h.ConnectAsync();

            h.Session.Dispatch(new TransferRequested(new UploadRequest("a", [file], ContainerPath.Root.Join("tmp").Join("berth-check"))));
            var state = await h.WaitFor(s => s.Transfers.Values.Any(t => t.Status == TransferStatus.Done));

            var (path, tar) = Assert.Single(api.Uploads);
            Assert.Equal("/tmp/berth-check", path);
            await using var reader = new ArchiveReader(new MemoryStream(tar));
            Assert.Equal("notes.txt", (await reader.NextAsync(CancellationToken.None))!.Name);
            Assert.Equal((1, 5L, (long?)5), (Assert.Single(state.Transfers.Values).Summary!.Files, state.Transfers.Values.Single().Summary!.Bytes, state.Transfers.Values.Single().Total));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task An_upload_into_a_missing_folder_fails_with_the_engines_message_before_sending_anything()
    {
        var folder = TransferFolder();
        try
        {
            var file = Path.Combine(folder, "notes.txt");
            await File.WriteAllTextAsync(file, "hello");
            var api = new FakeEngineApi();
            await using var h = Harness.With(api);
            await h.ConnectAsync();

            h.Session.Dispatch(new TransferRequested(new UploadRequest("a", [file], ContainerPath.Root.Join("nope"))));
            var state = await h.WaitFor(s => s.Transfers.Values.Any(t => t.Status == TransferStatus.Failed));

            Assert.Equal("Could not find the file /nope in container a", Assert.Single(state.Transfers.Values).Error);
            Assert.Empty(api.Uploads);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Advances the clock a batch at a time until the state matches: the transfer reads on its own thread.</summary>
    private static async Task<EngineState> TickUntil(Harness h, Func<EngineState, bool> match)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!match(h.Session.State) && DateTime.UtcNow < deadline)
        {
            h.Time.Advance(EngineSession.TransferBatchInterval);
            await Task.Delay(10);
        }

        return await h.WaitFor(match);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }
}

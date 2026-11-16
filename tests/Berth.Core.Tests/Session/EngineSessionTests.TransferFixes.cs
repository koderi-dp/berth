using Berth.Engine;
using Berth.Engine.Models;
using Berth.Files;
using Berth.Session;
using Berth.Tests.Engine;
using Berth.Tests.Files;

namespace Berth.Tests.Session;

/// <summary>The review's fixes to transfers: local read errors, the replace question against the container, exit, timeouts.</summary>
public sealed partial class EngineSessionTests
{
    private static readonly PathStat FolderThere = new("berth-check", 4096, PathStat.DirectoryBit | 0x1ed, null, "");

    private static readonly ContainerPath CheckFolder = ContainerPath.Root.Join("tmp").Join("berth-check");

    [Fact]
    public async Task An_upload_of_a_file_that_cannot_be_read_fails_before_sending_and_keeps_the_connection()
    {
        var folder = TransferFolder();
        try
        {
            var file = Path.Combine(folder, "notes.txt");
            await File.WriteAllTextAsync(file, "hello");
            var api = new FakeEngineApi();
            api.PathStats["/tmp/berth-check"] = FolderThere;
            await using var h = Harness.With(api);
            await h.ConnectAsync();

            FileTransfer failed;
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                h.Session.Dispatch(new TransferRequested(new UploadRequest("a", [file], CheckFolder)));
                failed = (await h.WaitFor(s => s.Transfers.Values.Any(t => t.Status == TransferStatus.Failed))).Transfers.Values.Single();
            }

            Assert.Equal("1 file could not be read (in use by another program, removed, or not allowed). Nothing was sent.", failed.Error);
            Assert.Equal(ConnectionStatus.Connected, h.Session.State.Status);
            Assert.Empty(api.Uploads);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task An_upload_over_a_same_named_entry_in_the_container_waits_for_a_yes_and_sends_nothing_until_then()
    {
        var folder = TransferFolder();
        try
        {
            var file = Path.Combine(folder, "notes.txt");
            await File.WriteAllTextAsync(file, "hello");
            var api = new FakeEngineApi();
            api.PathStats["/tmp/berth-check"] = FolderThere;
            api.PathStats["/tmp/berth-check/notes.txt"] = new PathStat("notes.txt", 3, 0x1a4, null, "");
            await using var h = Harness.With(api);
            await h.ConnectAsync();
            var request = new UploadRequest("a", [file], CheckFolder);

            h.Session.Dispatch(new TransferRequested(request));
            var asking = (await h.WaitFor(s => s.Transfers.Values.Any(t => t.Status == TransferStatus.Asking))).Transfers.Values.Single();

            Assert.Equal(["notes.txt"], asking.Asks.ToArray());
            Assert.Empty(api.Uploads);
            Assert.Contains("StatPath /tmp/berth-check/notes.txt", api.Calls);

            h.Session.Dispatch(new TransferDismissed(asking.Id));
            h.Session.Dispatch(new TransferRequested(request with { Replace = ["notes.txt"] }));
            await h.WaitFor(s => s.Transfers.Values.Any(t => t.Status == TransferStatus.Done));

            Assert.Single(api.Uploads);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_timed_out_engine_call_fails_the_transfer_without_losing_the_connection()
    {
        var folder = TransferFolder();
        try
        {
            var api = new FakeEngineApi();
            api.Fail("StatPath", new EngineException(0, "The engine did not answer within 30 seconds.") { TimedOut = true });
            await using var h = Harness.With(api);
            await h.ConnectAsync();

            h.Session.Dispatch(new TransferRequested(new DownloadRequest("a", [ContainerPath.Root.Join("big")], folder, Replace: false)));
            var state = await h.WaitFor(s => s.Transfers.Values.Any(t => t.Status == TransferStatus.Failed));

            Assert.Equal(ConnectionStatus.Connected, state.Status);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Stopping_the_transfers_on_exit_removes_a_running_downloads_partial_and_waits_for_it()
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
            await TickUntil(h, s => s.Transfers.Values.Any(t => t.Done > 0));
            Assert.NotEmpty(Directory.GetFileSystemEntries(folder));

            var stopped = await h.Session.StopTransfersAsync(TimeSpan.FromSeconds(5));

            Assert.True(stopped);
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

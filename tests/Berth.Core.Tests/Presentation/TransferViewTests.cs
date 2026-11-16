using System.Collections.Immutable;
using Berth.Files;
using Berth.Presentation;
using Berth.Session;

namespace Berth.Tests.Presentation;

/// <summary>A transfer's row: its title, its progress line and bar, its summary, and what can be done with it.</summary>
public sealed class TransferViewTests
{
    private static readonly ContainerPath Hosts = ContainerPath.Root.Join("etc").Join("hosts");

    private static FileTransfer Download(TransferStatus status, long done = 0, long? total = null, TransferSummary? summary = null, string? error = null, int sources = 1) =>
        new(7, new DownloadRequest("a", [.. Enumerable.Repeat(Hosts, sources)], @"C:\Downloads", false), status, done, total, summary, error);

    private static FileTransfer Upload(TransferStatus status, TransferSummary? summary = null) =>
        new(8, new UploadRequest("a", [@"C:\x\notes.txt"], ContainerPath.Root.Join("tmp")), status, 0, null, summary, null);

    private static TransferLine Line(FileTransfer transfer) => TransferView.Of(ImmutableDictionary<int, FileTransfer>.Empty.Add(transfer.Id, transfer), "a").Single();

    [Fact]
    public void Titles_say_what_goes_where()
    {
        Assert.Equal("Downloading hosts to C:\\Downloads", Line(Download(TransferStatus.Running)).Title);
        Assert.Equal("Downloading 3 items to C:\\Downloads", Line(Download(TransferStatus.Running, sources: 3)).Title);
        Assert.Equal("Uploading notes.txt to /tmp", Line(Upload(TransferStatus.Running)).Title);
        Assert.Equal("Downloaded hosts to C:\\Downloads", Line(Download(TransferStatus.Done, summary: TransferSummary.Empty)).Title);
        Assert.Equal("Uploaded notes.txt to /tmp", Line(Upload(TransferStatus.Done, TransferSummary.Empty)).Title);
        Assert.Equal("Download of hosts to C:\\Downloads", Line(Download(TransferStatus.Cancelled)).Title);
        Assert.Equal("Upload of notes.txt to /tmp", Line(Upload(TransferStatus.Failed)).Title);
    }

    [Fact]
    public void A_running_transfer_shows_its_bytes_and_share_when_the_total_is_known()
    {
        var known = Line(Download(TransferStatus.Running, 1024 * 1024, 4 * 1024 * 1024));
        var unknown = Line(Download(TransferStatus.Running, 1024 * 1024));

        Assert.Equal("1 MB of 4 MB (25%)", known.Text);
        Assert.Equal(0.25, known.Fraction);
        Assert.Equal("1 MB so far", unknown.Text);
        Assert.Null(unknown.Fraction);
        Assert.True(known.CanCancel);
        Assert.False(known.CanDismiss);
        Assert.Null(known.ShowInFolder);
    }

    [Fact]
    public void A_finished_download_sums_up_and_offers_show_in_folder()
    {
        var summary = new TransferSummary(3, 1, 4 * 1024 * 1024, 0, 0, 0, 0, 0, @"C:\Downloads\etc");

        var line = Line(Download(TransferStatus.Done, summary: summary));

        Assert.Equal("3 files and 1 folder, 4 MB.", line.Text);
        Assert.Null(line.Notes);
        Assert.Equal(@"C:\Downloads\etc", line.ShowInFolder);
        Assert.True(line.CanDismiss);
        Assert.False(line.CanCancel);
    }

    [Fact]
    public void What_was_left_out_is_said_with_why()
    {
        var summary = new TransferSummary(1, 0, 9, Links: 2, Special: 1, Unsafe: 1, Renamed: 1, Clashes: 1, @"C:\d\x");

        var line = Line(Download(TransferStatus.Done, summary: summary));

        Assert.Equal(
            "Skipped 2 links (never created or followed), 1 device or FIFO, 1 path that would leave the folder and 1 name that clashed on Windows. Renamed 1 name Windows cannot hold.",
            line.Notes);
    }

    [Fact]
    public void One_file_reads_in_the_singular()
    {
        Assert.Equal("1 file, 9 B.", Line(Upload(TransferStatus.Done, new TransferSummary(1, 0, 9, 0, 0, 0, 0, 0, null))).Text);
    }

    [Fact]
    public void A_cancelled_upload_says_it_cannot_be_undone_and_a_download_that_nothing_partial_is_left()
    {
        Assert.Equal(
            "Cancelled. What the engine unpacked before the cancel stays in the container: an upload cannot be undone.",
            Line(Upload(TransferStatus.Cancelled)).Text);
        Assert.Equal("Cancelled. Nothing partial was left in the folder.", Line(Download(TransferStatus.Cancelled)).Text);
    }

    [Fact]
    public void A_failure_shows_its_message_as_it_is()
    {
        var line = Line(Download(TransferStatus.Failed, error: "Could not find the file /etc/hosts in container a"));

        Assert.Equal("Could not find the file /etc/hosts in container a", line.Text);
        Assert.True(line.IsError);
        Assert.True(line.CanDismiss);
    }

    [Fact]
    public void An_interrupted_transfer_says_the_connection_was_lost()
    {
        Assert.Equal("Stopped: the connection to the engine was lost.", Line(Download(TransferStatus.Interrupted)).Text);
    }

    [Fact]
    public void An_upload_waiting_for_an_answer_says_so_and_can_only_be_dismissed()
    {
        var line = Line(Upload(TransferStatus.Asking) with { Asks = ["notes.txt"] });

        Assert.Equal("Waiting for your answer: notes.txt is already there.", line.Text);
        Assert.True(line.IsAsking);
        Assert.False(line.CanCancel);
        Assert.True(line.CanDismiss);
    }
}
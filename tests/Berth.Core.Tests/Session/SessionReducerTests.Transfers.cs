using Berth.Files;
using Berth.Session;

namespace Berth.Tests.Session;

/// <summary>The Files tab's downloads and uploads, tracked as pulls are.</summary>
public sealed partial class SessionReducerTests
{
    private static readonly DownloadRequest Download = new("a", [ContainerPath.Root.Join("etc")], @"C:\Temp\berth-check", Replace: false);

    private static readonly UploadRequest Upload = new("a", [@"C:\Temp\x.txt"], ContainerPath.Root.Join("tmp"));

    private static readonly TransferSummary Saved = new(3, 1, 300, 1, 0, 0, 0, 0, @"C:\Temp\berth-check\etc");

    private static (EngineState State, int Id) Transferring(FileTransferRequest request)
    {
        var (state, effects) = Reduce(Connected(), new TransferRequested(request));
        return (state, Assert.IsType<StartTransferEffect>(Assert.Single(effects)).Id);
    }

    [Fact]
    public void A_transfer_starts_with_a_new_id()
    {
        var connected = Connected();

        var (state, effects) = Reduce(connected, new TransferRequested(Download));

        var transfer = Assert.Single(state.Transfers.Values);
        Assert.Equal(TransferStatus.Running, transfer.Status);
        Assert.Equal(connected.Ticket + 1, transfer.Id);
        Assert.Equal([new StartTransferEffect(transfer.Id, Download)], effects);
        Assert.True(transfer.IsDownload);
    }

    [Fact]
    public void Several_transfers_run_at_once()
    {
        var (first, _) = Transferring(Download);

        var (state, _) = Reduce(first, new TransferRequested(Upload));

        Assert.Equal(2, state.Transfers.Count);
    }

    [Fact]
    public void A_transfer_is_not_started_while_not_connected()
    {
        var disconnected = Reduce(Connected(), new ConnectionLost("gone")).State;

        Assert.Same(disconnected, Reduce(disconnected, new TransferRequested(Download)).State);
    }

    [Fact]
    public void Progress_is_shown_and_the_end_keeps_the_summary()
    {
        var (state, id) = Transferring(Download);

        state = Reduce(state, new TransferProgressed(id, 100, 300)).State;
        Assert.Equal((100L, (long?)300), (state.Transfers[id].Done, state.Transfers[id].Total));

        state = Reduce(state, new TransferFinished(id, Saved)).State;
        Assert.Equal(TransferStatus.Done, state.Transfers[id].Status);
        Assert.Equal(Saved, state.Transfers[id].Summary);
        Assert.Equal(300, state.Transfers[id].Done);
    }

    [Fact]
    public void A_failure_keeps_its_message_as_it_is()
    {
        var (state, id) = Transferring(Download);

        state = Reduce(state, new TransferFailed(id, "Could not find the file /etc in container a", EngineUnreachable: false)).State;

        Assert.Equal(TransferStatus.Failed, state.Transfers[id].Status);
        Assert.Equal("Could not find the file /etc in container a", state.Transfers[id].Error);
        Assert.Equal(ConnectionStatus.Connected, state.Status);
    }

    [Fact]
    public void A_failure_because_the_engine_is_gone_loses_the_connection()
    {
        var (state, id) = Transferring(Download);

        state = Reduce(state, new TransferFailed(id, "connection refused", EngineUnreachable: true)).State;

        Assert.Equal(ConnectionStatus.Disconnected, state.Status);
        Assert.Equal(TransferStatus.Failed, state.Transfers[id].Status);
    }

    [Fact]
    public void Cancel_stops_a_running_transfer_and_keeps_it_as_cancelled()
    {
        var (state, id) = Transferring(Upload);

        var (cancelled, effects) = Reduce(state, new TransferCancelRequested(id));

        Assert.Equal(TransferStatus.Cancelled, cancelled.Transfers[id].Status);
        Assert.Equal([new CancelTransferEffect(id)], effects);
        Assert.Same(cancelled, Reduce(cancelled, new TransferProgressed(id, 5, null)).State);
        Assert.Same(cancelled, Reduce(cancelled, new TransferFailed(id, "x", false)).State);
    }

    [Fact]
    public void Only_an_ended_transfer_is_dismissed()
    {
        var (state, id) = Transferring(Download);
        Assert.Same(state, Reduce(state, new TransferDismissed(id)).State);

        var done = Reduce(state, new TransferFinished(id, Saved)).State;
        Assert.Empty(Reduce(done, new TransferDismissed(id)).State.Transfers);
    }

    [Fact]
    public void A_lost_connection_interrupts_running_transfers_and_keeps_ended_ones()
    {
        var (state, running) = Transferring(Download);
        var second = Reduce(state, new TransferRequested(Upload));
        var finished = Assert.IsType<StartTransferEffect>(Assert.Single(second.Effects)).Id;
        state = Reduce(second.State, new TransferFinished(finished, Saved)).State;

        state = Reduce(state, new ConnectionLost("gone")).State;

        Assert.Equal(TransferStatus.Interrupted, state.Transfers[running].Status);
        Assert.Equal(TransferStatus.Done, state.Transfers[finished].Status);
        Assert.Same(state, Reduce(state, new TransferFinished(running, Saved)).State);
    }

    [Fact]
    public void An_upload_that_would_replace_something_waits_for_an_answer_and_can_be_dismissed()
    {
        var (state, id) = Transferring(Upload);

        state = Reduce(state, new TransferNeedsAnswer(id, ["x.txt"])).State;

        Assert.Equal(TransferStatus.Asking, state.Transfers[id].Status);
        Assert.Equal(["x.txt"], state.Transfers[id].Asks.ToArray());
        Assert.Empty(Reduce(state, new TransferDismissed(id)).State.Transfers);
    }

    [Fact]
    public void A_transfer_that_finished_after_its_cancel_was_asked_for_is_done()
    {
        var (state, id) = Transferring(Download);
        state = Reduce(state, new TransferCancelRequested(id)).State;

        state = Reduce(state, new TransferFinished(id, Saved)).State;

        Assert.Equal(TransferStatus.Done, state.Transfers[id].Status);
        Assert.Equal(Saved, state.Transfers[id].Summary);
    }
}
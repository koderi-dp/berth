using System.Collections.Immutable;

namespace Berth.Session;

/// <summary>The Files tab's downloads and uploads, tracked as pulls are: several at once, each with its progress, a cancel and how it ended.</summary>
public static partial class SessionReducer
{
    private static (EngineState State, ImmutableArray<SessionEffect> Effects)? ReduceTransfers(EngineState state, SessionEvent e, bool connected)
    {
        switch (e)
        {
            case TransferRequested request when connected:
            {
                var id = state.Ticket + 1;
                var transfer = new FileTransfer(id, request.Request, TransferStatus.Running, 0, null, null, null);
                return (state with { Ticket = id, Transfers = state.Transfers.SetItem(id, transfer) }, [new StartTransferEffect(id, request.Request)]);
            }

            case TransferRequested:
                return (state, None);

            case TransferProgressed progress when IsTransferring(state, progress.Id, out var transfer):
                return (state with { Transfers = state.Transfers.SetItem(transfer.Id, transfer with { Done = progress.Done, Total = progress.Total }) }, None);

            case TransferFinished finished when IsTransferring(state, finished.Id, out var transfer):
            {
                var done = transfer with { Status = TransferStatus.Done, Summary = finished.Summary, Done = finished.Summary.Bytes };
                return (state with { Transfers = state.Transfers.SetItem(transfer.Id, done) }, None);
            }

            case TransferFailed failure when IsTransferring(state, failure.Id, out var transfer):
            {
                var failed = state with { Transfers = state.Transfers.SetItem(transfer.Id, transfer with { Status = TransferStatus.Failed, Error = failure.Message }) };
                return failure.EngineUnreachable ? Lose(failed, failure.Message) : (failed, None);
            }

            case TransferCancelRequested cancel when IsTransferring(state, cancel.Id, out var transfer):
                return (
                    state with { Transfers = state.Transfers.SetItem(transfer.Id, transfer with { Status = TransferStatus.Cancelled }) },
                    [new CancelTransferEffect(transfer.Id)]);

            case TransferDismissed dismissed when state.Transfers.TryGetValue(dismissed.Id, out var transfer) && transfer.Status != TransferStatus.Running:
                return (state with { Transfers = state.Transfers.Remove(transfer.Id) }, None);

            case TransferProgressed or TransferFinished or TransferFailed or TransferCancelRequested or TransferDismissed:
                return (state, None);

            default:
                return null;
        }
    }

    private static bool IsTransferring(EngineState state, int id, out FileTransfer transfer) =>
        state.Transfers.TryGetValue(id, out transfer!) && transfer.Status == TransferStatus.Running;

    /// <summary>The connection is over or lost: running transfers are interrupted (the runtime has stopped them); ended ones stay.</summary>
    private static ImmutableDictionary<int, FileTransfer> InterruptTransfers(ImmutableDictionary<int, FileTransfer> transfers) =>
        transfers.SetItems(transfers.Values
            .Where(t => t.Status == TransferStatus.Running)
            .Select(t => KeyValuePair.Create(t.Id, t with { Status = TransferStatus.Interrupted })));
}

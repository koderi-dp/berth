using Berth.Files;

namespace Berth.Session;

/// <summary>The runtime of the Files tab's downloads and uploads.</summary>
public sealed partial class EngineSession
{
    private const string DownloadFailedReport = "a download failed";
    private const string UploadFailedReport = "an upload failed";

    /// <summary>How often a running transfer's progress goes to the reducer, as one <see cref="TransferProgressed"/>.</summary>
    public static readonly TimeSpan TransferBatchInterval = TimeSpan.FromMilliseconds(100);

    private TransferRunner? _transfers;

    /// <summary>Starts or cancels a transfer; false for any other effect.</summary>
    private bool StartTransferEffect(SessionEffect effect)
    {
        switch (effect)
        {
            case StartTransferEffect start when _connection is { } connection:
                _transfers ??= new TransferRunner(this, connection);
                _transfers.Start(start.Id, start.Request);
                return true;

            case CancelTransferEffect cancel:
                _transfers?.Cancel(cancel.Id);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Stops the transfers of the connection being retired (a download removes what it saved under its temporary name).</summary>
    private void RetireTransfers()
    {
        _transfers?.Dispose();
        _transfers = null;
    }

    /// <summary>
    /// The transfers of one connection: each runs on the connection, and a timer sends each one's progress, when it changed, once a
    /// <see cref="TransferBatchInterval"/>. A transfer that ends sends its last progress and then how it ended; a cancelled one sends
    /// nothing more. <see cref="Start"/>, <see cref="Cancel"/> and <see cref="Dispose"/> are called only from the loop.
    /// </summary>
    private sealed class TransferRunner(EngineSession session, Connection connection) : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<Transfer> _transfers = [];
        private ITimer? _tick;

        public void Start(int id, FileTransferRequest request)
        {
            var transfer = new Transfer(id, CancellationTokenSource.CreateLinkedTokenSource(connection.Token));
            lock (_lock)
            {
                _transfers.Add(transfer);
                _tick ??= session._time.CreateTimer(_ => Send(), state: null, TransferBatchInterval, TransferBatchInterval);
            }

            var token = transfer.Stop.Token;
            connection.Run(_ => RunAsync(transfer, request, token));
        }

        public void Cancel(int id)
        {
            lock (_lock)
            {
                foreach (var transfer in _transfers.Where(t => t.Id == id).ToList())
                    Stop(transfer);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var transfer in _transfers.ToList())
                    Stop(transfer);
                _tick?.Dispose();
                _tick = null;
            }
        }

        private async Task RunAsync(Transfer transfer, FileTransferRequest request, CancellationToken ct)
        {
            SessionEvent end;
            try
            {
                var api = connection.Api!;
                var summary = request switch
                {
                    DownloadRequest download => await ArchiveTransfer.DownloadAsync(
                        api, download.ContainerId, download.Sources, download.Destination, download.Replace, transfer.Add, transfer.Size, ct)
                        .ConfigureAwait(false),
                    UploadRequest upload => await ArchiveTransfer.UploadAsync(
                        api, upload.ContainerId, upload.Sources, upload.Destination, transfer.Add, transfer.Size, ct)
                        .ConfigureAwait(false),
                    _ => TransferSummary.Empty,
                };
                end = new TransferFinished(transfer.Id, summary);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                session.Report(request is DownloadRequest ? DownloadFailedReport : UploadFailedReport, ex);
                end = new TransferFailed(transfer.Id, ex.Message, IsUnreachable(ex));
            }

            lock (_lock)
            {
                if (ct.IsCancellationRequested)
                    return;
                Flush(transfer);
                session.Post(end, connection);
                Stop(transfer);
            }
        }

        private void Send()
        {
            lock (_lock)
            {
                foreach (var transfer in _transfers)
                    Flush(transfer);
            }
        }

        /// <summary>Called under the lock.</summary>
        private void Flush(Transfer transfer)
        {
            if (transfer.TakeChange() is { } progress)
                session.Post(new TransferProgressed(transfer.Id, progress.Done, progress.Total), connection);
        }

        /// <summary>Called under the lock.</summary>
        private void Stop(Transfer transfer)
        {
            if (!_transfers.Remove(transfer))
                return;
            transfer.Stop.Cancel();
            transfer.Stop.Dispose();
            if (_transfers.Count == 0)
            {
                _tick?.Dispose();
                _tick = null;
            }
        }
    }

    /// <summary>One transfer's scope and progress; the progress is written by the transfer and read by the timer.</summary>
    private sealed class Transfer(int id, CancellationTokenSource stop)
    {
        private long _done;
        private long _total = -1;
        private int _changed;

        public int Id { get; } = id;

        public CancellationTokenSource Stop { get; } = stop;

        public void Add(long bytes)
        {
            Interlocked.Add(ref _done, bytes);
            Volatile.Write(ref _changed, 1);
        }

        public void Size(long total)
        {
            Volatile.Write(ref _total, total);
            Volatile.Write(ref _changed, 1);
        }

        /// <summary>The progress when it changed since last asked; null otherwise.</summary>
        public (long Done, long? Total)? TakeChange()
        {
            if (Interlocked.Exchange(ref _changed, 0) == 0)
                return null;
            var total = Volatile.Read(ref _total);
            return (Interlocked.Read(ref _done), total < 0 ? null : total);
        }
    }
}

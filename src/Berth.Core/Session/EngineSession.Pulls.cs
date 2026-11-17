using Berth.Engine.Models;

namespace Berth.Session;

/// <summary>The runtime of image pulls.</summary>
public sealed partial class EngineSession
{
    private const string PullFailedReport = "an image pull failed";

    /// <summary>How often a running pull's latest progress lines go to the reducer, as one <see cref="PullProgressArrived"/>.</summary>
    public static readonly TimeSpan PullBatchInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Raised on a pull's thread each time a progress line of the pull of this reference is read. For tests.</summary>
    internal event Action<string>? PullLineRecorded;

    /// <summary>
    /// The image pulls of one connection: a stream per pull, and a timer that sends each pull's lines read since the last batch, once a
    /// <see cref="PullBatchInterval"/>, as one event per pull. A pull that ends sends its last lines and then how it ended. <see cref="Start"/>,
    /// <see cref="Cancel"/> and <see cref="Dispose"/> are called only from the loop.
    /// </summary>
    private sealed class PullRunner(EngineSession session, Connection connection) : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<Pull> _pulls = [];
        private ITimer? _tick;

        public void Start(string reference, int generation)
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            var pull = new Pull(reference, generation, stop);
            lock (_lock)
            {
                _pulls.Add(pull);
                _tick ??= session._time.CreateTimer(_ => Send(), state: null, PullBatchInterval, PullBatchInterval);
            }

            var token = stop.Token;
            connection.Run(_ => FollowAsync(pull, token));
        }

        /// <summary>Stops every pull of <paramref name="reference"/>; it sends nothing more.</summary>
        public void Cancel(string reference)
        {
            lock (_lock)
            {
                foreach (var pull in _pulls.Where(p => p.Reference == reference).ToList())
                    Stop(pull);
            }
        }

        /// <summary>Stops the timer and every pull.</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var pull in _pulls.ToList())
                    Stop(pull);
                _tick?.Dispose();
                _tick = null;
            }
        }

        private async Task FollowAsync(Pull pull, CancellationToken ct)
        {
            SessionEvent end;
            try
            {
                await foreach (var line in connection.Api!.PullImageAsync(pull.Reference, ct).ConfigureAwait(false))
                {
                    lock (_lock)
                    {
                        if (!ct.IsCancellationRequested)
                            pull.Fresh.Add(line);
                    }

                    session.Raise(session.PullLineRecorded, pull.Reference);
                }

                end = new PullFinished(pull.Reference, pull.Generation);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                session.Report(PullFailedReport, ex);
                end = new PullFailed(pull.Reference, pull.Generation, ex.Message, IsUnreachable(ex)) { Ssh = SshOf(ex) };
            }

            lock (_lock)
            {
                if (ct.IsCancellationRequested)
                    return;

                // Under the lock, so the timer cannot send these lines after the end.
                Flush(pull);
                session.Post(end, connection);
                Stop(pull);
            }
        }

        /// <summary>Sends each pull's lines read since the last batch. Called by the timer.</summary>
        private void Send()
        {
            lock (_lock)
            {
                foreach (var pull in _pulls)
                    Flush(pull);
            }
        }

        /// <summary>Called under the lock.</summary>
        private void Flush(Pull pull)
        {
            if (pull.Fresh.Count == 0)
                return;
            session.Post(new PullProgressArrived(pull.Reference, pull.Generation, [.. pull.Fresh]), connection);
            pull.Fresh.Clear();
        }

        /// <summary>Forgets <paramref name="pull"/> and cancels its stream; the timer stops with the last pull. Called under the lock.</summary>
        private void Stop(Pull pull)
        {
            if (!_pulls.Remove(pull))
                return;
            pull.Stop.Cancel();
            pull.Stop.Dispose();
            if (_pulls.Count == 0)
            {
                _tick?.Dispose();
                _tick = null;
            }
        }
    }

    private sealed class Pull(string reference, int generation, CancellationTokenSource stop)
    {
        public string Reference { get; } = reference;

        public int Generation { get; } = generation;

        public CancellationTokenSource Stop { get; } = stop;

        /// <summary>Lines read since the last batch. Guarded by the runner's lock.</summary>
        public List<PullProgress> Fresh { get; } = [];
    }
}

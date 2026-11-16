using System.Collections.Immutable;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Session;

public sealed partial class EngineSession
{
    private const string StatsFailedReport = "a container stats stream failed";

    /// <summary>How often the latest list stats go to the reducer, as one <see cref="StatsBatchArrived"/>.</summary>
    public static readonly TimeSpan StatsBatchInterval = TimeSpan.FromSeconds(1);

    /// <summary>Raised on a stream's thread each time a list stats sample of the container with this id is read. For tests.</summary>
    internal event Action<string>? StatsSampleRecorded;

    /// <summary>
    /// Follows one container's stats for the list until cancelled or the stream ends. A stream that ends (the container
    /// stopped) or finds the container gone is not a failure; any other failure is reported and leaves the other streams be.
    /// </summary>
    private async Task FollowStatsAsync(Connection connection, StatsWatch watch, string id, CancellationToken ct)
    {
        try
        {
            await foreach (var sample in connection.Api!.StreamStatsAsync(id, ct).ConfigureAwait(false))
            {
                watch.Record(id, sample, ct);
                Raise(StatsSampleRecorded, id);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (EngineException ex) when (ex.StatusCode == 404)
        {
        }
        catch (Exception ex)
        {
            Report(StatsFailedReport, ex);
        }
    }

    /// <summary>
    /// The list stats of one connection: a stream per watched container and a timer that sends the latest sample of each,
    /// once a <see cref="StatsBatchInterval"/>, as one event. Everything but <see cref="Record"/> and the timer is called only
    /// from the loop.
    /// </summary>
    private sealed class StatsWatch(EngineSession session, Connection connection) : IDisposable
    {
        private readonly Dictionary<string, (CancellationTokenSource Stop, Task Stream)> _streams = new(StringComparer.Ordinal);
        private readonly Lock _lock = new();
        private Dictionary<string, StatsSample> _fresh = new(StringComparer.Ordinal);
        private ITimer? _tick;

        /// <summary>
        /// Follows exactly <paramref name="ids"/>: stops the streams of ids left out and starts those of new ids, or of ids
        /// whose stream has ended (the container was started again); the others carry on.
        /// </summary>
        public void Watch(ImmutableArray<string> ids)
        {
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            foreach (var id in _streams.Keys.Where(id => !wanted.Contains(id)).ToList())
                Stop(id);

            foreach (var id in ids)
            {
                if (_streams.TryGetValue(id, out var current))
                {
                    if (!current.Stream.IsCompleted)
                        continue;
                    Stop(id);
                }

                Start(id);
            }

            if (_streams.Count == 0)
            {
                _tick?.Dispose();
                _tick = null;
            }
            else
            {
                _tick ??= session._time.CreateTimer(_ => Send(), state: null, StatsBatchInterval, StatsBatchInterval);
            }
        }

        /// <summary>Keeps <paramref name="sample"/> for the next batch unless its stream was stopped. Called from the stream.</summary>
        public void Record(string id, StatsSample sample, CancellationToken ct)
        {
            lock (_lock)
            {
                if (!ct.IsCancellationRequested)
                    _fresh[id] = sample;
            }
        }

        /// <summary>Stops the timer and every stream.</summary>
        public void Dispose()
        {
            _tick?.Dispose();
            _tick = null;
            foreach (var id in _streams.Keys.ToList())
                Stop(id);
        }

        private void Start(string id)
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            var token = stop.Token;
            var stream = connection.Run(_ => session.FollowStatsAsync(connection, this, id, token));
            _streams[id] = (stop, stream);
        }

        private void Stop(string id)
        {
            var (stop, _) = _streams[id];
            _streams.Remove(id);
            stop.Cancel();
            stop.Dispose();
            lock (_lock)
                _fresh.Remove(id);
        }

        /// <summary>Sends the samples read since the last batch, if any. Called by the timer.</summary>
        private void Send()
        {
            ImmutableDictionary<string, StatsSample> batch;
            lock (_lock)
            {
                if (_fresh.Count == 0)
                    return;
                batch = _fresh.ToImmutableDictionary(StringComparer.Ordinal);
                _fresh = new Dictionary<string, StatsSample>(StringComparer.Ordinal);
            }

            session.Post(new StatsBatchArrived(batch), connection);
        }
    }
}

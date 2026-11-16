using Berth.Engine;

namespace Berth.Session;

public sealed partial class EngineSession
{
    /// <summary>
    /// One connection attempt and everything started on it: the engine client, its calls, its stream and the detail sessions
    /// opened on it.
    /// </summary>
    private sealed class Connection
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _calls = [];
        private readonly Lock _detailsLock = new();
        private readonly HashSet<ContainerDetailSession> _details = [];
        private bool _retired;

        /// <summary>Set by the connect call before it reports success, so every later call can use it.</summary>
        public IEngineApi? Api { get; set; }

        /// <summary>Cancelled when the connection is retired. Called only from the loop.</summary>
        public CancellationToken Token => _stop.Token;

        /// <summary>Runs <paramref name="call"/> on the thread pool. Called only from the loop.</summary>
        public Task Run(Func<CancellationToken, Task> call)
        {
            _calls.RemoveAll(t => t.IsCompleted);
            var token = _stop.Token;
            var task = Task.Run(() => call(token));
            _calls.Add(task);
            return task;
        }

        /// <summary>
        /// Creates a detail session on this connection's engine client and tracks it until the page disposes it, or returns
        /// null when the client is not set or the connection is retired. Safe from any thread.
        /// </summary>
        public ContainerDetailSession? Attach(Func<IEngineApi, ContainerDetailSession> create)
        {
            lock (_detailsLock)
            {
                if (_retired || Api is not { } api)
                    return null;

                var detail = create(api);
                _details.Add(detail);
                detail.ReleaseOnDispose(() =>
                {
                    lock (_detailsLock)
                        _details.Remove(detail);
                });
                return detail;
            }
        }

        /// <summary>
        /// Cancels every call and cuts off the detail sessions, waits for all of them to finish, then disposes the engine
        /// client.
        /// </summary>
        public async Task RetireAsync()
        {
            ContainerDetailSession[] details;
            lock (_detailsLock)
            {
                _retired = true;
                details = [.. _details];
                _details.Clear();
            }

            var calls = _calls.ToArray();
            try
            {
                _stop.Cancel();
                await Task.WhenAll(calls.Concat(details.Select(d => d.CutOffAsync()))).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Calls report their own failures as events; a throwing cancellation callback has no one to tell.
            }

            if (Api is { } api)
            {
                try
                {
                    await api.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The connection is already abandoned; a failing dispose changes nothing the user can act on.
                }
            }

            _stop.Dispose();
        }
    }
}

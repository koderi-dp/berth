using Berth.Connections;
using Berth.Engine;
using Berth.Engine.Models;
using Berth.Transport;

namespace Berth;

/// <summary>
/// The engine client for one connection attempt together with the transport it runs on. <see cref="EngineClient"/> disposes its
/// handler but leaves the transport to its owner (a TLS transport holds certificates), so this disposes both: the session disposes
/// its <see cref="IEngineApi"/> each time it reconnects, and nothing is left behind.
/// </summary>
internal sealed class EngineConnection : IEngineApi
{
    private readonly IEngineTransport _transport;
    private readonly EngineClient _client;

    private EngineConnection(IEngineTransport transport, EngineClient client)
    {
        _transport = transport;
        _client = client;
    }

    /// <summary>A client for <paramref name="profile"/>. Nothing is opened until the first call.</summary>
    public static IEngineApi Open(ConnectionProfile profile)
    {
        var transport = TransportFactory.Create(profile);
        return new EngineConnection(transport, new EngineClient(EngineHttp.CreateHandler(transport)));
    }

    public Task<EngineVersion> ConnectAsync(CancellationToken ct) => _client.ConnectAsync(ct);

    public Task<EngineInfo> GetInfoAsync(CancellationToken ct) => _client.GetInfoAsync(ct);

    public Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(CancellationToken ct) => _client.ListContainersAsync(ct);

    public Task StartContainerAsync(string id, CancellationToken ct) => _client.StartContainerAsync(id, ct);

    public Task StopContainerAsync(string id, CancellationToken ct) => _client.StopContainerAsync(id, ct);

    public Task RestartContainerAsync(string id, CancellationToken ct) => _client.RestartContainerAsync(id, ct);

    public Task PauseContainerAsync(string id, CancellationToken ct) => _client.PauseContainerAsync(id, ct);

    public Task UnpauseContainerAsync(string id, CancellationToken ct) => _client.UnpauseContainerAsync(id, ct);

    public Task RemoveContainerAsync(string id, bool force, CancellationToken ct) => _client.RemoveContainerAsync(id, force, ct);

    public Task<ContainerInspection> InspectContainerAsync(string id, CancellationToken ct) => _client.InspectContainerAsync(id, ct);

    public IAsyncEnumerable<LogLine> StreamLogsAsync(string id, bool tty, LogRequest request, CancellationToken ct) =>
        _client.StreamLogsAsync(id, tty, request, ct);

    public IAsyncEnumerable<EngineEventMessage> StreamEventsAsync(CancellationToken ct) => _client.StreamEventsAsync(ct);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}

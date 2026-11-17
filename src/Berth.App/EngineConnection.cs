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
    /// <param name="secrets">Where an SSH connection reads its password or passphrase when it signs in.</param>
    public static IEngineApi Open(ConnectionProfile profile, ISecretStore secrets)
    {
        var transport = TransportFactory.Create(profile, secrets);
        return new EngineConnection(transport, new EngineClient(EngineHttp.CreateHandler(transport), transport));
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

    public IAsyncEnumerable<StatsSample> StreamStatsAsync(string id, CancellationToken ct) => _client.StreamStatsAsync(id, ct);

    public IAsyncEnumerable<EngineEventMessage> StreamEventsAsync(CancellationToken ct) => _client.StreamEventsAsync(ct);

    public Task<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken ct) => _client.ListImagesAsync(ct);

    public Task<ImageInspection> InspectImageAsync(string idOrName, CancellationToken ct) => _client.InspectImageAsync(idOrName, ct);

    public Task<IReadOnlyList<ImageDeleteResult>> RemoveImageAsync(string idOrName, CancellationToken ct) => _client.RemoveImageAsync(idOrName, ct);

    public IAsyncEnumerable<PullProgress> PullImageAsync(string reference, CancellationToken ct) => _client.PullImageAsync(reference, ct);

    public Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct) => _client.ListVolumesAsync(ct);

    public Task<VolumeSummary> CreateVolumeAsync(string name, CancellationToken ct) => _client.CreateVolumeAsync(name, ct);

    public Task RemoveVolumeAsync(string name, CancellationToken ct) => _client.RemoveVolumeAsync(name, ct);

    public Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct) => _client.ListNetworksAsync(ct);

    public Task RemoveNetworkAsync(string id, CancellationToken ct) => _client.RemoveNetworkAsync(id, ct);

    public Task<DiskUsage> GetDiskUsageAsync(CancellationToken ct) => _client.GetDiskUsageAsync(ct);

    public Task<string> CreateContainerAsync(ContainerCreateRequest request, CancellationToken ct) => _client.CreateContainerAsync(request, ct);

    public Task<string> CreateExecAsync(
        string containerId, IReadOnlyList<string> command, TerminalSize size, IReadOnlyList<string> environment, CancellationToken ct) =>
        _client.CreateExecAsync(containerId, command, size, environment, ct);

    public Task RunDetachedExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct) =>
        _client.RunDetachedExecAsync(containerId, command, ct);

    public Task<ExecStream> StartExecAsync(string execId, TerminalSize size, CancellationToken ct) => _client.StartExecAsync(execId, size, ct);

    public Task ResizeExecAsync(string execId, TerminalSize size, CancellationToken ct) => _client.ResizeExecAsync(execId, size, ct);

    public Task<ExecInspection> InspectExecAsync(string execId, CancellationToken ct) => _client.InspectExecAsync(execId, ct);

    public Task<ExecOutput> RunExecForOutputAsync(string containerId, IReadOnlyList<string> command, int limit, CancellationToken ct) =>
        _client.RunExecForOutputAsync(containerId, command, limit, ct);

    public Task<PathStat> StatPathAsync(string containerId, string path, CancellationToken ct) => _client.StatPathAsync(containerId, path, ct);

    public Task<ArchiveRead> GetArchiveAsync(string containerId, string path, CancellationToken ct) => _client.GetArchiveAsync(containerId, path, ct);

    public Task PutArchiveAsync(string containerId, string path, Func<Stream, CancellationToken, Task> write, CancellationToken ct) =>
        _client.PutArchiveAsync(containerId, path, write, ct);

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

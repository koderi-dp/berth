namespace Berth.Transport;

/// <summary>Opens two-way byte streams to an engine.</summary>
public interface IEngineTransport : IAsyncDisposable
{
    /// <summary>Opens a new connected stream on each call. The caller disposes it.</summary>
    ValueTask<Stream> OpenAsync(CancellationToken ct);
}

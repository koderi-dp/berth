using System.IO.Pipes;

namespace Berth.Transport;

/// <summary>Reaches an engine over a Windows named pipe. A pipe that does not answer within five seconds throws <see cref="TimeoutException"/>.</summary>
public sealed class NamedPipeTransport(string serverName, string pipeName) : IEngineTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async ValueTask<Stream> OpenAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(serverName, pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectTimeout, ct).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

using System.Net.Security;
using System.Net.Sockets;

namespace Berth.Engine;

/// <summary>
/// The <see cref="ExecStream"/> <see cref="EngineClient"/> hands out: the connection the engine switched after its answer, with the
/// bytes the engine sent with the answer's headers read first.
/// </summary>
internal sealed class UpgradedExecStream : ExecStream
{
    private readonly Stream _connection;
    private ReadOnlyMemory<byte> _early;

    /// <param name="connection">The connection, owned from now on.</param>
    /// <param name="early">Bytes of the stream that were read with the answer's headers.</param>
    internal UpgradedExecStream(Stream connection, ReadOnlyMemory<byte> early)
    {
        _connection = connection;
        _early = early;
    }

    /// <summary>
    /// On TCP the socket's sending side is shut down; on TLS a close_notify is written. Other connections (Docker Desktop's pipe,
    /// which is in byte mode) have no half to close: false.
    /// </summary>
    public override async Task<bool> CloseWriteAsync()
    {
        switch (_connection)
        {
            case NetworkStream network:
                network.Socket.Shutdown(SocketShutdown.Send);
                return true;
            case SslStream ssl:
                await ssl.ShutdownAsync().ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    public override int Read(Span<byte> buffer) => _early.IsEmpty ? _connection.Read(buffer) : TakeEarly(buffer);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _early.IsEmpty ? _connection.ReadAsync(buffer, cancellationToken) : ValueTask.FromResult(TakeEarly(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(ReadOnlySpan<byte> buffer) => _connection.Write(buffer);

    public override void Write(byte[] buffer, int offset, int count) => _connection.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _connection.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _connection.WriteAsync(buffer, offset, count, cancellationToken);

    public override void Flush() => _connection.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _connection.FlushAsync(cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _connection.Dispose();
        base.Dispose(disposing);
    }

    private int TakeEarly(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, _early.Length);
        _early.Span[..count].CopyTo(buffer);
        _early = _early[count..];
        return count;
    }
}

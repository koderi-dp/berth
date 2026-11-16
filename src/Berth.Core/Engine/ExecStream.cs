using System.Net.Security;
using System.Net.Sockets;

namespace Berth.Engine;

/// <summary>
/// The two-way raw stream of a started exec: the connection the engine switched after its answer. A read gives the shell's output
/// (bytes the engine sent with its answer's headers first) and returns 0 once the engine has closed the stream; a write is the
/// shell's input. Reads and writes throw what the transport throws, and one read may run alongside one write. Disposing it closes
/// the connection.
/// </summary>
public sealed class ExecStream : Stream
{
    private readonly Stream _connection;
    private ReadOnlyMemory<byte> _early;

    /// <param name="connection">The connection, owned from now on.</param>
    /// <param name="early">Bytes of the stream that were read with the answer's headers.</param>
    internal ExecStream(Stream connection, ReadOnlyMemory<byte> early)
    {
        _connection = connection;
        _early = early;
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Closes Berth's side of the stream, so the engine reads the end of the input, while the shell's output can still be read: on TCP
    /// the socket's sending side is shut down, on TLS a close_notify is sent. A named pipe has no such half: nothing is done and false
    /// is returned. Writing afterwards throws.
    /// </summary>
    public async Task<bool> CloseWriteAsync()
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

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

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

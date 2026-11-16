namespace Berth.Engine;

/// <summary>
/// The two-way raw stream of a started exec, as <see cref="IEngineApi.StartExecAsync"/> returns it. A read gives the shell's output
/// and returns 0 once the engine has closed the stream; a write is the shell's input. One read may run alongside one write. Reads and
/// writes throw what the transport throws. Disposing it closes the connection.
/// </summary>
public abstract class ExecStream : Stream
{
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
    /// Closes Berth's side of the stream, so the engine reads the end of the input, while the shell's output can still be read.
    /// Returns false when the connection has no such half (Docker Desktop's byte-mode pipe): nothing is done, and disposing the stream
    /// is then the way to end it. It may write on the connection (on TLS it sends a close_notify), so it must not run while a write is
    /// pending: call it only after the last write has finished. Writing afterwards throws.
    /// </summary>
    public abstract Task<bool> CloseWriteAsync();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

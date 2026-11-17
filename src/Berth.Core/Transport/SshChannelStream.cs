using Berth.Connections;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Berth.Transport;

/// <summary>
/// One <c>docker system dial-stdio</c> on an SSH session, as a two-way stream: what is written goes to the command's standard input, a
/// read gives its standard output (stderr is kept apart and read only when the command fails). A read returns 0 once the command ended
/// with status 0. When it ended otherwise, a read throws <see cref="SshTransportException"/>: the remote's failure (no docker CLI, docker
/// refused, dial-stdio's own error, with its stderr) or the session dropping. A cancelled read ends the stream (SSH.NET's reads do not
/// stop on a token), which is what the HTTP client does with a connection whose read it cancels anyway.
/// </summary>
internal sealed class SshChannelStream : Stream, IHalfClosable
{
    /// <summary>How long a read that saw the output end waits for the command's exit status.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    private readonly SshCommand _command;
    private readonly Task _run;
    private readonly Stream _input;
    private readonly Action<SshChannelStream> _closed;
    private int _disposed;
    private volatile bool _dropped;

    private SshChannelStream(SshCommand command, Task run, Stream input, Action<SshChannelStream> closed)
    {
        _command = command;
        _run = run;
        _input = input;
        _closed = closed;
    }

    /// <summary>
    /// Starts <paramref name="commandText"/> on <paramref name="client"/> and returns its stream. A channel the server refuses throws
    /// SSH.NET's <see cref="SshException"/> (the caller tells that apart). <paramref name="closed"/> is called once when the stream is
    /// disposed.
    /// </summary>
    public static SshChannelStream Start(SshClient client, string commandText, Action<SshChannelStream> closed)
    {
        var command = client.CreateCommand(commandText);
        try
        {
            var run = command.ExecuteAsync();
            // Its failure is read through ExitStatus and the stream; never left unobserved.
            _ = run.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return new SshChannelStream(command, run, command.CreateInputStream(), closed);
        }
        catch
        {
            command.Dispose();
            throw;
        }
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

    /// <summary>The session under this stream ended: the next read that sees the end throws <see cref="SshFailure.Dropped"/>.</summary>
    public void Drop()
    {
        _dropped = true;
        // Ends a read waiting for output.
        _command.OutputStream.Dispose();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int read;
        await using (cancellationToken.Register(static state => ((SshCommand)state!).OutputStream.Dispose(), _command))
        {
            read = await _command.OutputStream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        }

        if (read > 0 || buffer.Length == 0)
            return read;
        cancellationToken.ThrowIfCancellationRequested();
        await EndedAsync().ConfigureAwait(false);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => _input.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _input.Write(buffer);

    /// <summary>SSH.NET sends on the calling thread, waiting only for the channel's window; done here so a write is never queued twice.</summary>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _input.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => _input.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        _input.Flush();
        return Task.CompletedTask;
    }

    /// <summary>Sends the channel's EOF: dial-stdio closes its write side to the engine, and the engine's answer can still be read.</summary>
    public Task CloseWriteAsync()
    {
        _input.Dispose();
        return Task.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _input.Dispose();
            }
            catch (Exception)
            {
                // The channel may be gone already; closing it below is all that is left to do.
            }

            _command.Dispose();
            _closed(this);
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// The output ended: a normal end when the command exited with 0; otherwise the remote's failure, or the session's when it dropped
    /// or the command never reported how it ended.
    /// </summary>
    private async Task EndedAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            await _run.WaitAsync(ExitWait).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new SshTransportException(new SshProblem(SshFailure.Dropped, ""), ex);
        }

        if (_dropped)
            throw new SshTransportException(new SshProblem(SshFailure.Dropped, ""));
        if (_command.ExitStatus is 0)
            return;
        throw new SshTransportException(SshDialFailure.Classify(_command.ExitStatus, _command.Error ?? ""));
    }
}

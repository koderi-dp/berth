using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Berth.Engine.Models;

namespace Berth.Engine;

/// <summary>
/// Splits the engine's multiplexed log stream into frames. Each frame is an 8 byte header (stream id, three zero bytes, payload
/// length as a big-endian 32-bit number) and then the payload. A container with a TTY sends no frames; see
/// <see cref="IEngineApi.StreamLogsAsync"/>.
/// </summary>
public static class LogFrames
{
    private const int HeaderLength = 8;

    /// <summary>The longest frame accepted, far above anything the engine sends; a longer one means the stream is not framed.</summary>
    public const int MaxFrameLength = 16 * 1024 * 1024;

    /// <summary>
    /// Yields each frame's stream and payload from a stream that delivers arbitrary chunks. Stream id 2 is stderr; any other id
    /// counts as stdout. A frame with no payload is skipped. When the stream ends inside a frame, that frame is dropped and the
    /// enumeration ends. A header announcing more than <see cref="MaxFrameLength"/> bytes (for example a TTY container's plain log
    /// read as framed) throws <see cref="EngineException"/> at once rather than waiting for that many bytes. The stream stays owned
    /// by the caller.
    /// </summary>
    public static async IAsyncEnumerable<(LogStream Stream, ReadOnlyMemory<byte> Payload)> ReadAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (TryReadFrame(ref buffer, out var logStream, out var payload))
                {
                    if (!payload.IsEmpty)
                    {
                        yield return (logStream, payload.ToArray());
                    }
                }

                if (result.IsCompleted)
                {
                    yield break;
                }

                // Keep the partial frame and ask for more than is buffered, so a frame longer than one read still completes.
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out LogStream stream, out ReadOnlySequence<byte> payload)
    {
        stream = LogStream.Stdout;
        payload = default;
        if (buffer.Length < HeaderLength)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        buffer.Slice(0, HeaderLength).CopyTo(header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (length > MaxFrameLength)
        {
            // The engine did answer (200); its stream just cannot be read as frames.
            throw new EngineException(200, "The log stream is not in the expected format.");
        }

        if (buffer.Length - HeaderLength < length)
        {
            return false;
        }

        stream = header[0] == 2 ? LogStream.Stderr : LogStream.Stdout;
        payload = buffer.Slice(HeaderLength, length);
        buffer = buffer.Slice(payload.End);
        return true;
    }
}

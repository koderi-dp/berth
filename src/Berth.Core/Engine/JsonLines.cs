using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace Berth.Engine;

/// <summary>Splits a newline-delimited stream, such as the engine's event stream, into lines.</summary>
public static class JsonLines
{
    /// <summary>
    /// Yields one UTF-8 line at a time from a stream that delivers arbitrary chunks, without its <c>\n</c> or <c>\r\n</c>. Blank
    /// lines are skipped. A last line without a newline is yielded when the stream ends. The stream stays owned by the caller.
    /// </summary>
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (buffer.PositionOf((byte)'\n') is { } end)
                {
                    var line = TrimEnd(buffer.Slice(0, end));
                    buffer = buffer.Slice(buffer.GetPosition(1, end));
                    if (!IsBlank(line))
                    {
                        yield return line.ToArray();
                    }
                }

                if (result.IsCompleted)
                {
                    var last = TrimEnd(buffer);
                    if (!IsBlank(last))
                    {
                        yield return last.ToArray();
                    }

                    yield break;
                }

                // Keep the partial line and ask for more than is buffered, so a line longer than one read still completes.
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static ReadOnlySequence<byte> TrimEnd(ReadOnlySequence<byte> line) =>
        !line.IsEmpty && line.Slice(line.Length - 1).FirstSpan[0] == (byte)'\r' ? line.Slice(0, line.Length - 1) : line;

    private static bool IsBlank(ReadOnlySequence<byte> line)
    {
        foreach (var segment in line)
        {
            foreach (var b in segment.Span)
            {
                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
                {
                    return false;
                }
            }
        }

        return true;
    }
}

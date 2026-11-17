using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Berth.Engine.Models;

namespace Berth.Engine;

/// <summary>Turns a container's log stream, as requested with <c>timestamps=true</c>, into <see cref="LogLine"/>s.</summary>
internal static class LogLines
{
    /// <summary>The longest line kept, in bytes; the rest of a longer line is dropped.</summary>
    public const int MaxLineBytes = 64 * 1024;

    private const string Ellipsis = "…";
    private const int TtyReadSize = 16 * 1024;

    /// <summary>
    /// Yields the lines of <paramref name="stream"/>: framed (see <see cref="LogFrames"/>) or, for a TTY container, plain text
    /// that is all stdout. Lines are put together per stream, so a line split across frames is whole and a stderr frame never
    /// lands inside a stdout line. A line ends at <c>\n</c> or <c>\r\n</c>; a last line without one is yielded when the stream
    /// ends. Bytes that are not UTF-8 become U+FFFD; a line longer than <see cref="MaxLineBytes"/> is cut there with "…".
    /// A leading RFC 3339 timestamp and its space become <see cref="LogLine.Timestamp"/>; ANSI codes are removed, the colours they set kept as
    /// <see cref="LogLine.Spans"/>.
    /// </summary>
    public static async IAsyncEnumerable<LogLine> ReadAsync(Stream stream, bool tty, [EnumeratorCancellation] CancellationToken ct)
    {
        var stdout = new LineAssembler(LogStream.Stdout);
        var stderr = new LineAssembler(LogStream.Stderr);
        var completed = new List<LogLine>();

        var chunks = tty ? ReadPlainAsync(stream, ct) : LogFrames.ReadAsync(stream, ct);
        await foreach (var (from, payload) in chunks.ConfigureAwait(false))
        {
            (from == LogStream.Stderr ? stderr : stdout).Append(payload.Span, completed);
            foreach (var line in completed)
            {
                yield return line;
            }

            completed.Clear();
        }

        stdout.Finish(completed);
        stderr.Finish(completed);
        foreach (var line in completed)
        {
            yield return line;
        }
    }

    private static async IAsyncEnumerable<(LogStream Stream, ReadOnlyMemory<byte> Payload)> ReadPlainAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[TtyReadSize];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            yield return (LogStream.Stdout, buffer.AsMemory(0, read).ToArray());
        }
    }

    /// <summary>The partial line of one stream.</summary>
    private sealed class LineAssembler(LogStream stream)
    {
        private readonly ArrayBufferWriter<byte> _pending = new();

        // The line was cut at the limit: drop the rest of it, up to its newline.
        private bool _dropping;

        public void Append(ReadOnlySpan<byte> data, List<LogLine> completed)
        {
            while (true)
            {
                var newline = data.IndexOf((byte)'\n');
                var part = newline < 0 ? data : data[..newline];
                if (!_dropping)
                {
                    var room = MaxLineBytes - _pending.WrittenCount;
                    if (part.Length > room)
                    {
                        _pending.Write(part[..room]);
                        completed.Add(Cut());
                        _dropping = true;
                    }
                    else
                    {
                        _pending.Write(part);
                    }
                }

                if (newline < 0)
                {
                    return;
                }

                if (!_dropping)
                {
                    completed.Add(Complete());
                }

                _dropping = false;
                data = data[(newline + 1)..];
            }
        }

        /// <summary>Yields the last line when the stream ended without a newline after it.</summary>
        public void Finish(List<LogLine> completed)
        {
            if (!_dropping && _pending.WrittenCount > 0)
            {
                completed.Add(Complete());
            }
        }

        private LogLine Complete()
        {
            var bytes = _pending.WrittenSpan;
            if (bytes is [.., (byte)'\r'])
            {
                bytes = bytes[..^1];
            }

            var line = ToLine(Encoding.UTF8.GetString(bytes), "");
            _pending.ResetWrittenCount();
            return line;
        }

        /// <summary>The line cut at the limit, without a character the limit fell inside.</summary>
        private LogLine Cut()
        {
            // A decoder that is not flushed holds back an incomplete character at the end instead of replacing it.
            var bytes = _pending.WrittenSpan;
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[decoder.GetCharCount(bytes, flush: false)];
            decoder.GetChars(bytes, chars, flush: false);

            var line = ToLine(new string(chars), Ellipsis);
            _pending.ResetWrittenCount();
            return line;
        }

        private LogLine ToLine(string text, string suffix)
        {
            var space = text.IndexOf(' ');
            var timestamp = space > 0 ? EngineTime.Parse(text.AsSpan(0, space)) : null;
            var rest = timestamp is null ? text : text[(space + 1)..];
            var (plain, spans) = AnsiText.Style(rest);
            return new LogLine(stream, timestamp, plain + suffix) { Spans = spans };
        }
    }
}

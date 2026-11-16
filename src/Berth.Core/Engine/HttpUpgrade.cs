using System.Globalization;
using System.Text;

namespace Berth.Engine;

/// <summary>
/// One HTTP/1.1 request that asks the engine to switch its connection to a two-way raw stream (<c>Connection: Upgrade</c>,
/// <c>Upgrade: tcp</c>), written by hand on a stream from the transport, and the answer read from it: the status line and headers,
/// then the body of an error answer. Nothing after the answer's headers is read unless it is that body, so the first bytes of a
/// switched stream stay for <see cref="ExecStream"/>. A connection that ends early or an answer that is not HTTP throws
/// <see cref="IOException"/> with a fixed text.
/// </summary>
internal sealed class HttpUpgrade
{
    /// <summary>The most of an error body that is read; the engine's messages are short.</summary>
    private const int MaxBody = 64 * 1024;

    private const int MaxLine = 8 * 1024;

    private const int MaxHeaders = 100;

    private readonly Stream _connection;
    private byte[] _buffer = new byte[4096];
    private int _start;
    private int _end;

    private HttpUpgrade(Stream connection) => _connection = connection;

    /// <summary>The answer's status code.</summary>
    public int Status { get; private set; }

    /// <summary>The answer's reason phrase, such as "Conflict".</summary>
    public string Reason { get; private set; } = "";

    /// <summary>The answer's headers; names in any case, a repeated header's last value.</summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What was read past the headers and not taken yet: the first bytes of a switched stream.</summary>
    public ReadOnlyMemory<byte> Rest => _buffer.AsMemory(_start, _end - _start);

    /// <summary>Writes a POST of <paramref name="body"/> (JSON) to <paramref name="path"/> with the upgrade headers, then reads the answer's head.</summary>
    public static async Task<HttpUpgrade> SendAsync(Stream connection, string path, byte[] body, CancellationToken ct)
    {
        var head = string.Create(
            CultureInfo.InvariantCulture,
            $"POST {path} HTTP/1.1\r\nHost: docker\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: Upgrade\r\nUpgrade: tcp\r\n\r\n");
        var request = new byte[Encoding.ASCII.GetByteCount(head) + body.Length];
        var written = Encoding.ASCII.GetBytes(head, request);
        body.CopyTo(request, written);
        await connection.WriteAsync(request, ct).ConfigureAwait(false);
        await connection.FlushAsync(ct).ConfigureAwait(false);

        var answer = new HttpUpgrade(connection);
        await answer.ReadHeadAsync(ct).ConfigureAwait(false);
        return answer;
    }

    /// <summary>
    /// The body of the answer, by <c>Content-Length</c>, chunked, or up to the end of the connection when it has neither; at most
    /// 64 KB of it.
    /// </summary>
    public async Task<string> ReadBodyAsync(CancellationToken ct)
    {
        var body = new MemoryStream();
        if (Headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            while (body.Length < MaxBody)
            {
                var sizeLine = await ReadLineAsync(ct).ConfigureAwait(false);
                var semicolon = sizeLine.IndexOf(';');
                if (!int.TryParse(semicolon < 0 ? sizeLine : sizeLine[..semicolon], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0)
                    throw Unreadable();
                if (size == 0)
                    break;

                // A chunk is read only up to the cap, so one huge chunk cannot pass it; the rest of the answer is not read.
                var room = MaxBody - (int)body.Length;
                await CopyAsync(body, Math.Min(size, room), ct).ConfigureAwait(false);
                if (size > room)
                    break;
                await ReadLineAsync(ct).ConfigureAwait(false);
            }
        }
        else if (Headers.TryGetValue("Content-Length", out var length))
        {
            if (!int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                throw Unreadable();
            await CopyAsync(body, Math.Min(count, MaxBody), ct).ConfigureAwait(false);
        }
        else
        {
            while (body.Length < MaxBody)
            {
                if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
                    break;
                await CopyAsync(body, Math.Min(_end - _start, MaxBody - (int)body.Length), ct).ConfigureAwait(false);
            }
        }

        return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
    }

    private static IOException Unreadable() => new("The engine's answer could not be read.");

    private async Task ReadHeadAsync(CancellationToken ct)
    {
        // "HTTP/1.1 101 UPGRADED": the version, the code, and a reason that may be empty or hold spaces.
        var statusLine = await ReadLineAsync(ct).ConfigureAwait(false);
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
        {
            throw Unreadable();
        }

        Status = status;
        Reason = parts.Length > 2 ? parts[2] : "";
        for (var count = 0; ; count++)
        {
            var line = await ReadLineAsync(ct).ConfigureAwait(false);
            if (line.Length == 0)
                return;

            var colon = line.IndexOf(':');
            if (colon <= 0 || count >= MaxHeaders)
                throw Unreadable();
            Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
    }

    /// <summary>One line without its CRLF. The connection ending first throws.</summary>
    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var searched = 0;
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start + searched, _end - _start - searched);
            if (newline >= 0)
            {
                var end = newline > _start && _buffer[newline - 1] == '\r' ? newline - 1 : newline;
                var line = Encoding.ASCII.GetString(_buffer, _start, end - _start);
                _start = newline + 1;
                return line;
            }

            searched = _end - _start;
            if (searched >= MaxLine)
                throw Unreadable();
            if (!await FillAsync(ct).ConfigureAwait(false))
                throw new IOException("The engine closed the connection before it answered.");
        }
    }

    /// <summary>Moves <paramref name="count"/> bytes to <paramref name="target"/>, reading more as needed. The connection ending first throws.</summary>
    private async Task CopyAsync(MemoryStream target, int count, CancellationToken ct)
    {
        while (count > 0)
        {
            if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
                throw new IOException("The engine closed the connection before it answered.");

            var take = Math.Min(count, _end - _start);
            target.Write(_buffer, _start, take);
            _start += take;
            count -= take;
        }
    }

    /// <summary>Reads more from the connection after what is buffered; false when the connection has ended.</summary>
    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);

        var read = await _connection.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
        _end += read;
        return read > 0;
    }
}

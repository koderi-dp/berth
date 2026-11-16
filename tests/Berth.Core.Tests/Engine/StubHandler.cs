using System.Net;
using System.Text;
using System.Threading.Channels;

namespace Berth.Tests.Engine;

/// <summary>An engine stand-in: records each request and answers from a table keyed by path and query.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, Func<HttpContent> Content)> _answers = new(StringComparer.Ordinal);

    public List<(string Method, string PathAndQuery)> Requests { get; } = [];

    /// <summary>Each request's body as text, in the order of <see cref="Requests"/>; empty when it had none.</summary>
    public List<string> Bodies { get; } = [];

    /// <summary>Answers requests for <paramref name="pathAndQuery"/> with this status and body.</summary>
    public StubHandler On(string pathAndQuery, HttpStatusCode status, string body)
    {
        _answers[pathAndQuery] = (status, () => new StringContent(body, Encoding.UTF8, "application/json"));
        return this;
    }

    public StubHandler On(string pathAndQuery, string body) => On(pathAndQuery, HttpStatusCode.OK, body);

    /// <summary>Answers requests for <paramref name="pathAndQuery"/> with this status, these bytes and these headers.</summary>
    public StubHandler On(string pathAndQuery, HttpStatusCode status, byte[] body, params (string Name, string Value)[] headers)
    {
        _headers[pathAndQuery] = headers;
        _answers[pathAndQuery] = (status, () => new ByteArrayContent(body));
        return this;
    }

    private readonly Dictionary<string, (string Name, string Value)[]> _headers = new(StringComparer.Ordinal);

    /// <summary>Each request's body as bytes, in the order of <see cref="Requests"/>; empty when it had none.</summary>
    public List<byte[]> BodyBytes { get; } = [];

    /// <summary>Each request's content type, in the order of <see cref="Requests"/>; null when it had no body.</summary>
    public List<string?> ContentTypes { get; } = [];

    /// <summary>Answers requests for <paramref name="pathAndQuery"/> with 200 and a body that arrives as the test writes it.</summary>
    public StubHandler On(string pathAndQuery, ChunkStream body)
    {
        _answers[pathAndQuery] = (HttpStatusCode.OK, () => new StreamContent(body));
        return this;
    }

    /// <summary>A <c>/version</c> answer that reports this API version.</summary>
    public StubHandler WithVersion(string apiVersion) => On(
        "/version",
        $$"""{"Version":"29.9.3","ApiVersion":"{{apiVersion}}","MinAPIVersion":"1.40","Os":"linux","Arch":"amd64"}""");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pathAndQuery = request.RequestUri!.PathAndQuery;
        Requests.Add((request.Method.Method, pathAndQuery));
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        BodyBytes.Add(bytes);
        Bodies.Add(Encoding.UTF8.GetString(bytes));
        ContentTypes.Add(request.Content?.Headers.ContentType?.MediaType);

        var (status, content) = _answers.TryGetValue(pathAndQuery, out var answer)
            ? answer
            : (HttpStatusCode.NotFound, () => new StringContent("""{"message":"page not found"}""", Encoding.UTF8, "application/json"));

        var response = new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = content(),
        };
        foreach (var (name, value) in _headers.GetValueOrDefault(pathAndQuery) ?? [])
            response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }
}

/// <summary>
/// A read-only body that delivers exactly the chunks the test writes, in order, and waits for more until <see cref="End"/> or
/// <see cref="Fail"/>. A read never spans two chunks, so a chunk boundary in the test is a boundary the reader sees.
/// </summary>
internal sealed class ChunkStream : Stream
{
    // Each item is a byte[] chunk or the Exception the next read throws.
    private readonly Channel<object> _items = Channel.CreateUnbounded<object>();
    private ReadOnlyMemory<byte> _current;

    public ChunkStream Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public ChunkStream Write(byte[] chunk)
    {
        _items.Writer.TryWrite(chunk);
        return this;
    }

    /// <summary>The engine closes the stream: reads past the written chunks return 0.</summary>
    public ChunkStream End()
    {
        _items.Writer.TryComplete();
        return this;
    }

    /// <summary>The connection drops: the read after the written chunks throws <paramref name="error"/>.</summary>
    public void Fail(Exception error)
    {
        _items.Writer.TryWrite(error);
        _items.Writer.TryComplete();
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current.IsEmpty)
        {
            if (!await _items.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            _items.Reader.TryRead(out var item);
            _current = item switch
            {
                byte[] chunk => chunk,
                Exception error => throw error,
                _ => ReadOnlyMemory<byte>.Empty,
            };
        }

        var count = Math.Min(buffer.Length, _current.Length);
        _current[..count].CopyTo(buffer);
        _current = _current[count..];
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

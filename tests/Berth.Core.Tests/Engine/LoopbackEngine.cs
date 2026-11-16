using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Berth.Connections;
using Berth.Tests.Transport;

namespace Berth.Tests.Engine;

/// <summary>Which transport a <see cref="LoopbackEngine"/> listens on.</summary>
public enum LoopbackKind { Tcp, Tls, NamedPipe }

/// <summary>A request the loopback engine received: its request line, headers (names in any case) and body.</summary>
internal sealed record LoopbackRequest(string RequestLine, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// A tiny HTTP/1.1 engine on a real TCP port, a TLS port (with a throwaway CA the client trusts) or a named pipe. It answers
/// <c>GET /version</c> with API 1.56, records the next other request (<see cref="Request"/>) and answers it with <see cref="Reply"/>
/// written as given, in one write. When that answer switches the connection (a 101, or a 200 raw stream), it echoes what the
/// client writes until the client closes its side (<see cref="ClientEnded"/>) or the test closes the connection
/// (<see cref="CloseAsync"/>); <see cref="SendAsync"/> writes more on it.
/// </summary>
internal sealed class LoopbackEngine : IAsyncDisposable
{
    /// <summary>The engine's answer when it switches the connection to a two-way raw stream.</summary>
    public const string Upgraded =
        "HTTP/1.1 101 UPGRADED\r\nContent-Type: application/vnd.docker.raw-stream\r\nConnection: Upgrade\r\nUpgrade: tcp\r\n\r\n";

    /// <summary>The engine's answer to a start without the upgrade headers: the raw stream on a plain 200.</summary>
    public const string RawStreamOk = "HTTP/1.1 200 OK\r\nContent-Type: application/vnd.docker.raw-stream\r\n\r\n";

    private const int PipeBufferSize = 64 * 1024;

    private const string VersionBody = """{"Version":"29.0.0","ApiVersion":"1.56","MinAPIVersion":"1.24","Os":"linux","Arch":"amd64"}""";

    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<LoopbackRequest> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Stream> _switched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _clientEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _tasks = [];
    private readonly List<Stream> _connections = [];
    private readonly Lock _lock = new();
    private readonly TcpListener? _listener;
    private readonly X509Certificate2? _ca;
    private readonly X509Certificate2? _serverCertificate;
    private readonly string? _tempDir;
    private readonly string? _pipeName;

    private LoopbackEngine(LoopbackKind kind)
    {
        Kind = kind;
        if (kind == LoopbackKind.NamedPipe)
        {
            _pipeName = "berth-test-" + Guid.NewGuid().ToString("N");
            Profile = new ConnectionProfile("loopback", "Loopback", TransportKind.NamedPipe, ".", 0, _pipeName, null);
            return;
        }

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        TlsOptions? tls = null;
        if (kind == LoopbackKind.Tls)
        {
            _ca = TestCertificates.CreateCa("Berth Loopback CA");
            _serverCertificate = TestCertificates.CreateLeaf(_ca, "localhost", serverAuth: true, out _);
            _tempDir = Directory.CreateTempSubdirectory("berth-loopback-").FullName;
            var caPath = Path.Combine(_tempDir, "ca.pem");
            File.WriteAllText(caPath, _ca.ExportCertificatePem());
            tls = new TlsOptions(caPath, null, null);
        }

        Profile = new ConnectionProfile("loopback", "Loopback", TransportKind.Tcp, "127.0.0.1", port, "", tls);
    }

    public LoopbackKind Kind { get; }

    /// <summary>The connection profile that reaches this engine.</summary>
    public ConnectionProfile Profile { get; }

    /// <summary>The answer to the next request other than <c>GET /version</c>, written as given in one write.</summary>
    public string Reply { get; set; } = Upgraded;

    /// <summary>The first request other than <c>GET /version</c>.</summary>
    public Task<LoopbackRequest> Request => _request.Task;

    /// <summary>Completes when the client has closed its side of the switched connection (or the connection broke).</summary>
    public Task ClientEnded => _clientEnded.Task;

    /// <summary>Everything the engine read on the switched connection, in order.</summary>
    public MemoryStream Received { get; } = new();

    public static LoopbackEngine Start(LoopbackKind kind)
    {
        var engine = new LoopbackEngine(kind);
        engine.Track(Task.Run(engine.AcceptAsync));
        return engine;
    }

    /// <summary>A JSON error answer with a <c>Content-Length</c>, as the engine sends one.</summary>
    public static string JsonError(int status, string reason, string message)
    {
        var body = $$"""{"message":"{{message}}"}""";
        return $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
    }

    /// <summary>Writes <paramref name="bytes"/> on the switched connection.</summary>
    public async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        var stream = await _switched.Task.WaitAsync(ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The engine closes the switched connection (with a TLS close_notify on TLS), as it does when the shell exits.</summary>
    public async Task CloseAsync()
    {
        var stream = await _switched.Task.ConfigureAwait(false);
        if (stream is SslStream ssl)
            await ssl.ShutdownAsync().ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        Stream[] connections;
        Task[] tasks;
        lock (_lock)
        {
            connections = [.. _connections];
            tasks = [.. _tasks];
        }

        foreach (var connection in connections)
            await connection.DisposeAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The server's own end: an accept or a read stopped by the disposal.
        }

        _stop.Dispose();
        _serverCertificate?.Dispose();
        _ca?.Dispose();
        if (_tempDir is not null)
            Directory.Delete(_tempDir, recursive: true);
    }

    private void Track(Task task)
    {
        lock (_lock)
            _tasks.Add(task);
    }

    private async Task AcceptAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            Stream connection;
            if (_pipeName is not null)
            {
                // 64 KB buffers each way, as the engine's pipe has: with none a write waits until the other side reads, and a client
                // that writes twice before reading would wait for ever on an echo.
                var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    PipeBufferSize, PipeBufferSize);
                Remember(pipe);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                connection = pipe;
            }
            else
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                connection = client.GetStream();
                Remember(connection);
                if (_serverCertificate is not null)
                {
                    var ssl = new SslStream(connection, leaveInnerStreamOpen: false);
                    Remember(ssl);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _serverCertificate }, ct).ConfigureAwait(false);
                    connection = ssl;
                }
            }

            Track(Task.Run(() => ServeAsync(connection, ct), CancellationToken.None));
        }
    }

    private void Remember(Stream stream)
    {
        lock (_lock)
            _connections.Add(stream);
    }

    private async Task ServeAsync(Stream connection, CancellationToken ct)
    {
        while (await ReadRequestAsync(connection, ct).ConfigureAwait(false) is { } request)
        {
            if (request.RequestLine.StartsWith("GET /version ", StringComparison.Ordinal))
            {
                await WriteAsync(connection, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {VersionBody.Length}\r\n\r\n{VersionBody}", ct).ConfigureAwait(false);
                continue;
            }

            _request.TrySetResult(request);
            var reply = Reply;
            await WriteAsync(connection, reply, ct).ConfigureAwait(false);
            if (reply.StartsWith("HTTP/1.1 101", StringComparison.Ordinal) || reply.StartsWith(RawStreamOk, StringComparison.Ordinal))
            {
                _switched.TrySetResult(connection);
                await EchoAsync(connection, ct).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>Echoes what the client writes until it closes its side (or the connection breaks or is closed here).</summary>
    private async Task EchoAsync(Stream connection, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await connection.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                lock (Received)
                    Received.Write(buffer, 0, read);
                await connection.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                await connection.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The connection broke or was closed here: the client has ended either way.
        }

        _clientEnded.TrySetResult();
    }

    private static async Task WriteAsync(Stream connection, string text, CancellationToken ct)
    {
        await connection.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        await connection.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one request: byte by byte up to the blank line (so nothing after it is taken), then the body by its length.</summary>
    private static async Task<LoopbackRequest?> ReadRequestAsync(Stream connection, CancellationToken ct)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 4 || head[^4] != '\r' || head[^3] != '\n' || head[^2] != '\r' || head[^1] != '\n')
        {
            if (await connection.ReadAsync(one, ct).ConfigureAwait(false) == 0)
                return null;
            head.Add(one[0]);
        }

        var lines = Encoding.ASCII.GetString([.. head]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var body = new byte[headers.TryGetValue("Content-Length", out var length) ? int.Parse(length) : 0];
        await connection.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return new LoopbackRequest(lines[0], headers, Encoding.UTF8.GetString(body));
    }
}

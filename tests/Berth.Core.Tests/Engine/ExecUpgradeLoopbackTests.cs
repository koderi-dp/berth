using System.Security.Cryptography;
using System.Text;
using Berth.Engine;
using Berth.Engine.Models;
using Berth.Transport;

namespace Berth.Tests.Engine;

/// <summary>
/// Exec start over real connections: a loopback engine on TCP, TLS and a named pipe, reached through the transport, the handler and
/// the client Berth uses. The connection is switched to a two-way raw stream and carries bytes both ways.
/// </summary>
public sealed class ExecUpgradeLoopbackTests : IDisposable
{
    private static readonly TerminalSize Size = new(80, 24);

    // A safety net so a broken connection fails the test instead of hanging the run; no test waits on it.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public static TheoryData<LoopbackKind> Transports => [LoopbackKind.Tcp, LoopbackKind.Tls, LoopbackKind.NamedPipe];

    public void Dispose() => _timeout.Dispose();

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Start_sends_the_upgrade_request(LoopbackKind kind)
    {
        await using var engine = await Harness.StartAsync(kind, _timeout.Token);

        await using var stream = await engine.Client.StartExecAsync("e1", Size, _timeout.Token);

        var request = await engine.Server.Request.WaitAsync(_timeout.Token);
        Assert.Equal("POST /v1.56/exec/e1/start HTTP/1.1", request.RequestLine);
        Assert.Equal("tcp", request.Headers["Upgrade"]);
        Assert.Equal("Upgrade", request.Headers["Connection"]);
        Assert.Equal("""{"Detach":false,"Tty":true,"ConsoleSize":[24,80]}""", request.Body);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task The_stream_carries_bytes_both_ways(LoopbackKind kind)
    {
        var ct = _timeout.Token;
        await using var engine = await Harness.StartAsync(kind, ct);
        await using var stream = await engine.Client.StartExecAsync("e1", Size, ct);

        await stream.WriteAsync("ping"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
        var ping = new byte[4];
        await stream.ReadExactlyAsync(ping, ct);
        Assert.Equal("ping", Encoding.ASCII.GetString(ping));

        var sent = RandomNumberGenerator.GetBytes(1024 * 1024);
        var received = new byte[sent.Length];
        var writing = Task.Run(async () =>
        {
            await stream.WriteAsync(sent, ct);
            await stream.FlushAsync(ct);
        }, ct);
        await stream.ReadExactlyAsync(received, ct);
        await writing;

        Assert.True(sent.AsSpan().SequenceEqual(received), "The megabyte came back changed.");
        Assert.True(engine.Server.Received.ToArray().AsSpan(4).SequenceEqual(sent), "The engine received the megabyte changed.");
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task The_engine_closing_ends_the_read_with_zero(LoopbackKind kind)
    {
        var ct = _timeout.Token;
        await using var engine = await Harness.StartAsync(kind, ct);
        await using var stream = await engine.Client.StartExecAsync("e1", Size, ct);

        await engine.Server.SendAsync("exit\r\n"u8.ToArray(), ct);
        await engine.Server.CloseAsync();

        Assert.Equal("exit\r\n", await ReadToEndAsync(stream, ct));
        Assert.Equal(0, await stream.ReadAsync(new byte[16], ct));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Closing_the_write_side_ends_the_engines_read_and_keeps_the_output(LoopbackKind kind)
    {
        var ct = _timeout.Token;
        await using var engine = await Harness.StartAsync(kind, ct);
        await using var stream = Assert.IsType<ExecStream>(await engine.Client.StartExecAsync("e1", Size, ct));
        await stream.WriteAsync("ping"u8.ToArray(), ct);
        await stream.FlushAsync(ct);

        var closed = await stream.CloseWriteAsync();

        if (kind == LoopbackKind.NamedPipe)
        {
            // A byte-mode pipe has no half to close: nothing changes and the stream goes on both ways.
            Assert.False(closed);
            await stream.WriteAsync("pong"u8.ToArray(), ct);
            var echoed = new byte[8];
            await stream.ReadExactlyAsync(echoed, ct);
            Assert.Equal("pingpong", Encoding.ASCII.GetString(echoed));
            return;
        }

        Assert.True(closed);
        await engine.Server.ClientEnded.WaitAsync(ct);
        await engine.Server.SendAsync("bye"u8.ToArray(), ct);
        await engine.Server.CloseAsync();
        Assert.Equal("pingbye", await ReadToEndAsync(stream, ct));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Disposing_the_stream_closes_the_connection(LoopbackKind kind)
    {
        var ct = _timeout.Token;
        await using var engine = await Harness.StartAsync(kind, ct);
        var stream = await engine.Client.StartExecAsync("e1", Size, ct);

        await stream.DisposeAsync();

        await engine.Server.ClientEnded.WaitAsync(ct);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Cancelling_a_pending_read_throws_OperationCanceledException(LoopbackKind kind)
    {
        var ct = _timeout.Token;
        await using var engine = await Harness.StartAsync(kind, ct);
        var stream = await engine.Client.StartExecAsync("e1", Size, ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var read = stream.ReadAsync(new byte[16], cancel.Token).AsTask();
        await Task.Delay(50, ct);
        Assert.False(read.IsCompleted);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await stream.DisposeAsync();
        await engine.Server.ClientEnded.WaitAsync(ct);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Bytes_sent_right_after_the_101_headers_are_not_lost(LoopbackKind kind)
    {
        var ct = _timeout.Token;
        await using var engine = await Harness.StartAsync(kind, ct);
        engine.Server.Reply = LoopbackEngine.Upgraded + "hello";

        await using var stream = await engine.Client.StartExecAsync("e1", Size, ct);

        var hello = new byte[5];
        await stream.ReadExactlyAsync(hello, ct);
        Assert.Equal("hello", Encoding.ASCII.GetString(hello));
    }

    private static async Task<string> ReadToEndAsync(Stream stream, CancellationToken ct)
    {
        using var all = new MemoryStream();
        await stream.CopyToAsync(all, ct);
        return Encoding.ASCII.GetString(all.ToArray());
    }

    /// <summary>A loopback engine and a connected client that reaches it through the transport its profile describes.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly IEngineTransport _transport;

        private Harness(LoopbackEngine server, IEngineTransport transport, EngineClient client)
        {
            Server = server;
            _transport = transport;
            Client = client;
        }

        public LoopbackEngine Server { get; }

        public EngineClient Client { get; }

        public static async Task<Harness> StartAsync(LoopbackKind kind, CancellationToken ct)
        {
            var server = LoopbackEngine.Start(kind);
            var transport = TransportFactory.Create(server.Profile);
            var client = new EngineClient(EngineHttp.CreateHandler(transport), transport);
            var harness = new Harness(server, transport, client);
            await client.ConnectAsync(ct);
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _transport.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}

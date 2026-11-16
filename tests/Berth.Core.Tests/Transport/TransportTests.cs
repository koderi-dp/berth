using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Berth.Connections;
using Berth.Transport;

namespace Berth.Tests.Transport;

public sealed class TransportTests : IDisposable
{
    private static readonly byte[] Ping = Encoding.ASCII.GetBytes("ping");

    private readonly string _tempDir = Directory.CreateTempSubdirectory("berth-transport-").FullName;

    // A safety net so a broken transport fails the test instead of hanging the run; no test waits on it.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose()
    {
        _timeout.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Tcp_stream_reaches_a_listener()
    {
        var ct = _timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = EchoOnceAsync(listener, ct);

        await using var transport = new TcpTransport("127.0.0.1", Port(listener), tls: null);
        await using (var stream = await transport.OpenAsync(ct))
        {
            Assert.Equal("ping", await RoundTripAsync(stream, ct));
        }

        await server;
    }

    [Fact]
    public async Task Tcp_to_a_closed_port_throws_SocketException()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = Port(listener);
            listener.Stop();
        }

        await using var transport = new TcpTransport("127.0.0.1", port, tls: null);

        await Assert.ThrowsAsync<SocketException>(async () => await transport.OpenAsync(_timeout.Token));
    }

    [Fact]
    public async Task Tcp_open_honours_cancellation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var transport = new TcpTransport("127.0.0.1", Port(listener), tls: null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await transport.OpenAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Named_pipe_stream_reaches_a_server()
    {
        var ct = _timeout.Token;
        var pipeName = "berth-test-" + Guid.NewGuid().ToString("N");
        await using var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var server = Task.Run(async () =>
        {
            await serverPipe.WaitForConnectionAsync(ct);
            await EchoAsync(serverPipe, ct);
        }, ct);

        await using var transport = new NamedPipeTransport(".", pipeName);
        await using (var stream = await transport.OpenAsync(ct))
        {
            Assert.Equal("ping", await RoundTripAsync(stream, ct));
        }

        await server;
    }

    [Fact]
    public async Task Named_pipe_that_does_not_exist_throws_within_five_seconds()
    {
        await using var transport = new NamedPipeTransport(".", "berth-test-missing-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<TimeoutException>(async () => await transport.OpenAsync(_timeout.Token));
    }

    [Fact]
    public async Task Tls_stream_reaches_a_server_whose_ca_is_trusted()
    {
        var ct = _timeout.Token;
        using var ca = TestCertificates.CreateCa("Berth Test CA");
        using var serverCert = TestCertificates.CreateLeaf(ca, "localhost", serverAuth: true, out _);
        var caPath = WritePem("ca.pem", ca.ExportCertificatePem());

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = TlsEchoOnceAsync(listener, serverCert, requireClientCertificate: false, ct);

        await using var transport = new TcpTransport("127.0.0.1", Port(listener), new TlsOptions(caPath, null, null));
        await using (var stream = await transport.OpenAsync(ct))
        {
            Assert.Equal("ping", await RoundTripAsync(stream, ct));
        }

        Assert.Equal("", await server);
    }

    [Fact]
    public async Task Tls_rejects_a_server_signed_by_another_ca()
    {
        var ct = _timeout.Token;
        using var trustedCa = TestCertificates.CreateCa("Berth Trusted CA");
        using var otherCa = TestCertificates.CreateCa("Berth Other CA");
        using var serverCert = TestCertificates.CreateLeaf(otherCa, "localhost", serverAuth: true, out _);
        var caPath = WritePem("ca.pem", trustedCa.ExportCertificatePem());

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = TlsEchoOnceAsync(listener, serverCert, requireClientCertificate: false, ct);

        await using var transport = new TcpTransport("127.0.0.1", Port(listener), new TlsOptions(caPath, null, null));

        await Assert.ThrowsAsync<AuthenticationException>(async () => await transport.OpenAsync(ct));
        Assert.Null(await server);
    }

    [Fact]
    public async Task Tls_presents_the_client_certificate_when_one_is_set()
    {
        var ct = _timeout.Token;
        using var ca = TestCertificates.CreateCa("Berth Test CA");
        using var serverCert = TestCertificates.CreateLeaf(ca, "localhost", serverAuth: true, out _);
        using var clientCert = TestCertificates.CreateLeaf(ca, "berth-client", serverAuth: false, out var clientKeyPem);
        var caPath = WritePem("ca.pem", ca.ExportCertificatePem());
        var certPath = WritePem("cert.pem", clientCert.ExportCertificatePem());
        var keyPath = WritePem("key.pem", clientKeyPem);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = TlsEchoOnceAsync(listener, serverCert, requireClientCertificate: true, ct);

        await using var transport = new TcpTransport("127.0.0.1", Port(listener), new TlsOptions(caPath, certPath, keyPath));
        await using (var stream = await transport.OpenAsync(ct))
        {
            Assert.Equal("ping", await RoundTripAsync(stream, ct));
        }

        Assert.Equal("CN=berth-client", await server);
    }

    [Fact]
    public void Factory_picks_the_transport_for_each_kind()
    {
        var tcp = new ConnectionProfile("a", "Tcp", TransportKind.Tcp, "127.0.0.1", 2375, "", null);
        var tls = tcp with { Port = 2376, Tls = new TlsOptions("ca.pem", null, null) };
        var pipe = new ConnectionProfile("b", "Pipe", TransportKind.NamedPipe, ".", 0, "docker_engine", null);

        Assert.IsType<TcpTransport>(TransportFactory.Create(tcp));
        Assert.IsType<TcpTransport>(TransportFactory.Create(tls));
        Assert.IsType<NamedPipeTransport>(TransportFactory.Create(pipe));
    }

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static async Task<string> RoundTripAsync(Stream stream, CancellationToken ct)
    {
        await stream.WriteAsync(Ping, ct);
        await stream.FlushAsync(ct);
        var buffer = new byte[Ping.Length];
        await stream.ReadExactlyAsync(buffer, ct);
        return Encoding.ASCII.GetString(buffer);
    }

    private static async Task EchoAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[Ping.Length];
        await stream.ReadExactlyAsync(buffer, ct);
        await stream.WriteAsync(buffer, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task EchoOnceAsync(TcpListener listener, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await EchoAsync(client.GetStream(), ct);
    }

    /// <summary>
    /// Accepts one TLS client and echoes. Returns the client certificate's subject ("" when none was sent), or null when the client
    /// rejected the connection. Under TLS 1.3 the server's handshake finishes before the client checks the certificate, so a rejection
    /// can surface on the read as well as in the handshake.
    /// </summary>
    private static async Task<string?> TlsEchoOnceAsync(TcpListener listener, X509Certificate2 serverCert, bool requireClientCertificate, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCert,
                ClientCertificateRequired = requireClientCertificate,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }, ct);
            await EchoAsync(ssl, ct);
        }
        catch (Exception e) when (e is AuthenticationException or IOException)
        {
            return null;
        }

        return ssl.RemoteCertificate?.Subject ?? "";
    }

    private string WritePem(string fileName, string pem)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, pem);
        return path;
    }
}

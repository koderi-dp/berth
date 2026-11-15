using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Berth.Connections;

namespace Berth.Transport;

/// <summary>
/// Reaches an engine over TCP, with TLS when <paramref name="tls"/> is set. TLS trusts only the CA in <see cref="TlsOptions.CaPath"/> and
/// still checks the host name; when a client certificate and key are set, they are presented to the engine. The certificate files are
/// read on the first TLS connection and kept until the transport is disposed.
/// </summary>
public sealed class TcpTransport(string host, int port, TlsOptions? tls) : IEngineTransport
{
    private readonly Lazy<TlsMaterial>? _tlsMaterial = tls is null ? null : new Lazy<TlsMaterial>(() => TlsMaterial.Load(tls));

    public async ValueTask<Stream> OpenAsync(CancellationToken ct)
    {
        var network = await ConnectAsync(ct).ConfigureAwait(false);
        if (_tlsMaterial is null)
        {
            return network;
        }

        var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(_tlsMaterial.Value.ClientOptions(host), ct).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_tlsMaterial is { IsValueCreated: true })
        {
            _tlsMaterial.Value.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask<NetworkStream> ConnectAsync(CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The trusted roots and the optional client certificate, loaded once from the profile's files.</summary>
    private sealed class TlsMaterial : IDisposable
    {
        private readonly X509Certificate2Collection _roots;
        private readonly X509Certificate2? _clientCertificate;
        private readonly SslStreamCertificateContext? _clientContext;

        private TlsMaterial(X509Certificate2Collection roots, X509Certificate2? clientCertificate)
        {
            _roots = roots;
            _clientCertificate = clientCertificate;
            _clientContext = clientCertificate is null ? null : SslStreamCertificateContext.Create(clientCertificate, additionalCertificates: null, offline: true);
        }

        public static TlsMaterial Load(TlsOptions options)
        {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPemFile(options.CaPath);
            if (options.CertPath is null || options.KeyPath is null)
            {
                return new TlsMaterial(roots, null);
            }

            // A key loaded from PEM is ephemeral, which Windows TLS cannot use; a PKCS#12 round trip gives it a usable key.
            using var fromPem = X509Certificate2.CreateFromPemFile(options.CertPath, options.KeyPath);
            var clientCertificate = X509CertificateLoader.LoadPkcs12(fromPem.Export(X509ContentType.Pkcs12), password: null);
            return new TlsMaterial(roots, clientCertificate);
        }

        public SslClientAuthenticationOptions ClientOptions(string targetHost)
        {
            var chainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            chainPolicy.CustomTrustStore.AddRange(_roots);

            return new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                CertificateChainPolicy = chainPolicy,
                ClientCertificateContext = _clientContext,
            };
        }

        public void Dispose()
        {
            foreach (var root in _roots)
            {
                root.Dispose();
            }

            _clientCertificate?.Dispose();
        }
    }
}

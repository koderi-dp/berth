using Berth.Connections;

namespace Berth.Transport;

/// <summary>Builds the transport a connection profile describes.</summary>
public static class TransportFactory
{
    /// <summary>The transport for a TCP or named pipe connection, which needs no secret.</summary>
    public static IEngineTransport Create(ConnectionProfile profile) => Create(profile, NoSecrets.Instance);

    /// <param name="secrets">Where an SSH connection's password or passphrase is read from, when it signs in.</param>
    public static IEngineTransport Create(ConnectionProfile profile, ISecretStore secrets) => profile.Kind switch
    {
        TransportKind.Tcp => new TcpTransport(profile.Host, profile.Port, profile.Tls),
        TransportKind.NamedPipe => new NamedPipeTransport(profile.Host, profile.PipeName),
        TransportKind.Ssh => new SshTransport(profile, secrets),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Kind, "Unknown transport kind."),
    };

    /// <summary>A store with nothing in it: an SSH connection made with it asks for its secret.</summary>
    private sealed class NoSecrets : ISecretStore
    {
        public static readonly NoSecrets Instance = new();

        public string? Get(string connectionId, SecretKind kind) => null;

        public void Set(string connectionId, SecretKind kind, string secret) => throw new NotSupportedException("This store keeps nothing.");

        public void Delete(string connectionId, SecretKind kind)
        {
        }
    }
}

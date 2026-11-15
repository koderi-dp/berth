using Berth.Connections;

namespace Berth.Transport;

/// <summary>Builds the transport a connection profile describes.</summary>
public static class TransportFactory
{
    public static IEngineTransport Create(ConnectionProfile profile) => profile.Kind switch
    {
        TransportKind.Tcp => new TcpTransport(profile.Host, profile.Port, profile.Tls),
        TransportKind.NamedPipe => new NamedPipeTransport(profile.Host, profile.PipeName),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Kind, "Unknown transport kind."),
    };
}

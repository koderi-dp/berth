using System.Globalization;

namespace Berth.Connections;

/// <summary>Reads and writes engine addresses in the form <c>DOCKER_HOST</c> uses: <c>tcp://host:port</c> or <c>npipe:////./pipe/name</c>.</summary>
public static class EngineAddress
{
    public const int DefaultPort = 2375;
    public const int DefaultTlsPort = 2376;
    public const string DefaultPipe = "docker_engine";

    private const string TcpScheme = "tcp://";
    private const string PipeScheme = "npipe://";

    /// <summary>
    /// Parses <paramref name="text"/>. On success <paramref name="error"/> is empty; on failure it says what is wrong, in a sentence
    /// that can be shown to the user, and the other outputs are empty. A TCP address without a port gets <see cref="DefaultPort"/>.
    /// </summary>
    public static bool TryParse(string text, out TransportKind kind, out string host, out int port, out string pipeName, out string error)
    {
        kind = TransportKind.Tcp;
        host = "";
        port = 0;
        pipeName = "";
        error = "";

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            error = "Enter an address.";
            return false;
        }

        if (trimmed.StartsWith(TcpScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseTcp(trimmed[TcpScheme.Length..], out host, out port, out error))
            {
                host = "";
                port = 0;
                return false;
            }

            return true;
        }

        if (trimmed.StartsWith(PipeScheme, StringComparison.OrdinalIgnoreCase))
        {
            // "npipe:////./pipe/name" is the documented form; "npipe://./pipe/name" is accepted too.
            var parts = trimmed[PipeScheme.Length..].TrimStart('/').Split('/');
            if (parts.Length != 3 || parts[0].Length == 0 || !parts[1].Equals("pipe", StringComparison.OrdinalIgnoreCase) || parts[2].Length == 0)
            {
                error = "A pipe address looks like npipe:////./pipe/docker_engine.";
                return false;
            }

            kind = TransportKind.NamedPipe;
            host = parts[0];
            pipeName = parts[2];
            return true;
        }

        error = "Use tcp:// or npipe://.";
        return false;
    }

    /// <summary>The address of <paramref name="profile"/>: <c>tcp://host:port</c> (an IPv6 host in brackets) or <c>npipe:////server/pipe/name</c>.</summary>
    public static string Format(ConnectionProfile profile) =>
        profile.Kind switch
        {
            TransportKind.NamedPipe => "npipe:////" + (profile.Host.Length == 0 ? "." : profile.Host) + "/pipe/" + profile.PipeName,
            _ => TcpScheme + (profile.Host.Contains(':') ? "[" + profile.Host + "]" : profile.Host) + ":" + profile.Port.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>Parses the part after <c>tcp://</c>: <c>host</c>, <c>host:port</c>, <c>[ipv6]</c> or <c>[ipv6]:port</c>, with an optional trailing slash.</summary>
    private static bool TryParseTcp(string authority, out string host, out int port, out string error)
    {
        host = "";
        port = DefaultPort;
        error = "";

        if (authority.EndsWith('/'))
        {
            authority = authority[..^1];
        }

        string? portText = null;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
            {
                error = "An IPv6 address needs a closing bracket, like tcp://[::1]:2375.";
                return false;
            }

            host = authority[1..close];
            var rest = authority[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    error = "Use tcp://host or tcp://host:port.";
                    return false;
                }

                portText = rest[1..];
            }
        }
        else
        {
            var colon = authority.IndexOf(':');
            if (colon >= 0 && authority.IndexOf(':', colon + 1) >= 0)
            {
                error = "Put an IPv6 address in brackets, like tcp://[::1]:2375.";
                return false;
            }

            host = colon < 0 ? authority : authority[..colon];
            portText = colon < 0 ? null : authority[(colon + 1)..];
        }

        if (host.Length == 0)
        {
            error = "The address has no host.";
            return false;
        }

        if (host.Contains('/'))
        {
            error = "Use tcp://host or tcp://host:port.";
            return false;
        }

        if (portText is not null
            && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            error = "The port must be between 1 and 65535.";
            return false;
        }

        return true;
    }
}

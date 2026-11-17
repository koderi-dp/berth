using System.Globalization;

namespace Berth.Connections;

/// <summary>An address as <see cref="EngineAddress"/> reads it. <see cref="User"/> is set for SSH only, <see cref="PipeName"/> for a pipe only.</summary>
public sealed record ParsedAddress(TransportKind Kind, string Host, int Port, string PipeName, string User);

/// <summary>
/// Reads and writes engine addresses in the form <c>DOCKER_HOST</c> uses: <c>tcp://host:port</c>, <c>npipe:////./pipe/name</c> or
/// <c>ssh://user@host:port</c>.
/// </summary>
public static class EngineAddress
{
    public const int DefaultPort = 2375;
    public const int DefaultTlsPort = 2376;
    public const int DefaultSshPort = 22;
    public const string DefaultPipe = "docker_engine";

    private const string TcpScheme = "tcp://";
    private const string PipeScheme = "npipe://";
    private const string SshScheme = "ssh://";

    /// <summary>
    /// Parses <paramref name="text"/> like <see cref="TryParse(string, out ParsedAddress, out string)"/>, without the SSH user name. On
    /// failure the other outputs are empty.
    /// </summary>
    public static bool TryParse(string text, out TransportKind kind, out string host, out int port, out string pipeName, out string error)
    {
        var ok = TryParse(text, out var address, out error);
        (kind, host, port, pipeName) = ok ? (address.Kind, address.Host, address.Port, address.PipeName) : (TransportKind.Tcp, "", 0, "");
        return ok;
    }

    /// <summary>
    /// Parses <paramref name="text"/>. On success <paramref name="error"/> is empty; on failure it says what is wrong, in a sentence
    /// that can be shown to the user, and <paramref name="address"/> is null. A TCP address without a port gets <see cref="DefaultPort"/>,
    /// an SSH one <see cref="DefaultSshPort"/>. An SSH address must name its user: Berth signs in itself, without an SSH config file.
    /// </summary>
    public static bool TryParse(string text, out ParsedAddress address, out string error)
    {
        address = null!;
        error = "";

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            error = "Enter an address.";
            return false;
        }

        if (trimmed.StartsWith(TcpScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseAuthority(trimmed[TcpScheme.Length..], DefaultPort, TcpScheme, "host", out var host, out var port, out error))
                return false;
            address = new ParsedAddress(TransportKind.Tcp, host, port, "", "");
            return true;
        }

        if (trimmed.StartsWith(SshScheme, StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[SshScheme.Length..];
            var at = rest.LastIndexOf('@');
            if (at <= 0)
            {
                error = "Put the user name in the address, like ssh://user@host.";
                return false;
            }

            var user = rest[..at];
            if (user.Contains(':'))
            {
                // ssh://user:password@host: the password would be saved, shown in commands and sent as the user name.
                error = "Do not put a password in the address: Berth asks for it and keeps it in Windows Credential Manager.";
                return false;
            }

            if (user.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                error = "The user name cannot hold spaces or control characters.";
                return false;
            }

            if (!TryParseAuthority(rest[(at + 1)..], DefaultSshPort, SshScheme, "user@host", out var host, out var port, out error))
                return false;
            address = new ParsedAddress(TransportKind.Ssh, host, port, "", user);
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

            address = new ParsedAddress(TransportKind.NamedPipe, parts[0], 0, parts[2], "");
            return true;
        }

        error = "Use tcp://, npipe:// or ssh://.";
        return false;
    }

    /// <summary>
    /// The address of <paramref name="profile"/>: <c>tcp://host:port</c> (an IPv6 host in brackets), <c>npipe:////server/pipe/name</c> or
    /// <c>ssh://user@host:port</c>.
    /// </summary>
    public static string Format(ConnectionProfile profile) =>
        profile.Kind switch
        {
            TransportKind.NamedPipe => "npipe:////" + (profile.Host.Length == 0 ? "." : profile.Host) + "/pipe/" + profile.PipeName,
            TransportKind.Ssh => SshScheme + (profile.Ssh?.User ?? "") + "@" + Authority(profile),
            _ => TcpScheme + Authority(profile),
        };

    private static string Authority(ConnectionProfile profile) =>
        (profile.Host.Contains(':') ? "[" + profile.Host + "]" : profile.Host) + ":" + profile.Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses the part after the scheme (and an SSH user): <c>host</c>, <c>host:port</c>, <c>[ipv6]</c> or <c>[ipv6]:port</c>, with an
    /// optional trailing slash. <paramref name="scheme"/> and <paramref name="hostWord"/> name the form in an error.
    /// </summary>
    private static bool TryParseAuthority(
        string authority, int defaultPort, string scheme, string hostWord, out string host, out int port, out string error)
    {
        host = "";
        port = defaultPort;
        error = "";
        var form = $"Use {scheme}{hostWord} or {scheme}{hostWord}:port.";
        var ipv6 = scheme + hostWord.Replace("host", "[::1]", StringComparison.Ordinal) + ":" + defaultPort.ToString(CultureInfo.InvariantCulture);

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
                error = $"An IPv6 address needs a closing bracket, like {ipv6}.";
                return false;
            }

            host = authority[1..close];
            var rest = authority[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    error = form;
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
                error = $"Put an IPv6 address in brackets, like {ipv6}.";
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
            error = form;
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

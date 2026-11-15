namespace Berth.Connections;

/// <summary>
/// A connection as the editor holds it while the user types: every field as entered. <see cref="TryBuild"/> checks it and gives the
/// <see cref="ConnectionProfile"/> to save.
/// </summary>
/// <param name="Address">The address in the <c>DOCKER_HOST</c> form (<see cref="EngineAddress"/>).</param>
/// <param name="UseTls">Whether a TCP connection uses TLS; ignored for a named pipe.</param>
public sealed record ConnectionDraft(string Name, string Address, bool UseTls, string CaPath, string CertPath, string KeyPath)
{
    public const string NameMissing = "Enter a name.";
    public const string CaMissing = "Choose the CA certificate file.";
    public const string ClientPairIncomplete = "Set both the client certificate and the client key, or neither.";

    /// <summary>A new connection: no name, the local engine's TCP address, no TLS.</summary>
    public static ConnectionDraft Empty { get; } = new("", "tcp://127.0.0.1:" + EngineAddress.DefaultPort, false, "", "", "");

    public static ConnectionDraft From(ConnectionProfile profile) =>
        new(
            profile.Name,
            EngineAddress.Format(profile),
            profile.Tls is not null,
            profile.Tls?.CaPath ?? "",
            profile.Tls?.CertPath ?? "",
            profile.Tls?.KeyPath ?? "");

    /// <summary>What is wrong with <see cref="Address"/>, in a sentence for the user, or null when it parses.</summary>
    public string? AddressError => EngineAddress.TryParse(Address, out _, out _, out _, out _, out var error) ? null : error;

    /// <summary>Whether <see cref="Address"/> is a named pipe address that parses; TLS does not apply to one.</summary>
    public bool IsNamedPipe => EngineAddress.TryParse(Address, out var kind, out _, out _, out _, out _) && kind == TransportKind.NamedPipe;

    /// <summary>
    /// Turns TLS on or off. A TCP address whose port is still the default for the other mode moves to this mode's default
    /// (<see cref="EngineAddress.DefaultPort"/> and <see cref="EngineAddress.DefaultTlsPort"/>); any other address is left as typed.
    /// </summary>
    public ConnectionDraft WithTls(bool on)
    {
        var (from, to) = on ? (EngineAddress.DefaultPort, EngineAddress.DefaultTlsPort) : (EngineAddress.DefaultTlsPort, EngineAddress.DefaultPort);
        if (!EngineAddress.TryParse(Address, out var kind, out var host, out var port, out _, out _) || kind != TransportKind.Tcp || port != from)
            return this with { UseTls = on };

        var moved = new ConnectionProfile("", "", TransportKind.Tcp, host, to, "", null);
        return this with { UseTls = on, Address = EngineAddress.Format(moved) };
    }

    /// <summary>
    /// The profile with id <paramref name="id"/> when every field is valid; otherwise false and <paramref name="error"/> says, in a
    /// sentence for the user, what to fix first (the name, then the address, then the TLS files).
    /// </summary>
    public bool TryBuild(string id, out ConnectionProfile profile, out string error)
    {
        profile = null!;

        var name = Name.Trim();
        if (name.Length == 0)
        {
            error = NameMissing;
            return false;
        }

        if (!EngineAddress.TryParse(Address, out var kind, out var host, out var port, out var pipeName, out error))
            return false;

        TlsOptions? tls = null;
        if (UseTls && kind == TransportKind.Tcp)
        {
            var ca = CaPath.Trim();
            if (ca.Length == 0)
            {
                error = CaMissing;
                return false;
            }

            var cert = NullIfBlank(CertPath);
            var key = NullIfBlank(KeyPath);
            if ((cert is null) != (key is null))
            {
                error = ClientPairIncomplete;
                return false;
            }

            tls = new TlsOptions(ca, cert, key);
        }

        profile = new ConnectionProfile(id, name, kind, host, port, pipeName, tls);
        return true;
    }

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

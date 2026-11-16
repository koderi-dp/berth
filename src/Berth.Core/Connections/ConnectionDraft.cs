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
    public const string StartCommandMissing = "Enter the start command, or turn engine control off.";
    public const string StopCommandMissing = "Enter the stop command, or turn engine control off.";

    /// <summary>Whether Berth starts and stops this connection's engine with <see cref="StartCommand"/> and <see cref="StopCommand"/>.</summary>
    public bool EngineControlOn { get; init; }

    /// <summary>The start command as typed (<see cref="CommandLine"/>); kept while engine control is off, but not saved.</summary>
    public string StartCommand { get; init; } = "";

    /// <summary>The stop command as typed; kept while engine control is off, but not saved.</summary>
    public string StopCommand { get; init; } = "";

    /// <summary>A new connection: no name, the local engine's TCP address, no TLS.</summary>
    public static ConnectionDraft Empty { get; } = new("", "tcp://127.0.0.1:" + EngineAddress.DefaultPort, false, "", "", "");

    public static ConnectionDraft From(ConnectionProfile profile) =>
        new(
            profile.Name,
            EngineAddress.Format(profile),
            profile.Tls is not null,
            profile.Tls?.CaPath ?? "",
            profile.Tls?.CertPath ?? "",
            profile.Tls?.KeyPath ?? "")
        {
            EngineControlOn = profile.EngineControl is not null,
            StartCommand = profile.EngineControl?.StartCommand ?? "",
            StopCommand = profile.EngineControl?.StopCommand ?? "",
        };

    /// <summary>Engine control on, with the two commands of <paramref name="control"/> (the WSL default the editor offers).</summary>
    public ConnectionDraft WithEngineControl(EngineControl control) =>
        this with { EngineControlOn = true, StartCommand = control.StartCommand, StopCommand = control.StopCommand };

    /// <summary>What is wrong with the start command while engine control is on, or null.</summary>
    public string? StartCommandError => EngineControlOn ? CommandError(StartCommand, StartCommandMissing, "Start command: ") : null;

    /// <summary>What is wrong with the stop command while engine control is on, or null.</summary>
    public string? StopCommandError => EngineControlOn ? CommandError(StopCommand, StopCommandMissing, "Stop command: ") : null;

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
    /// sentence for the user, what to fix first (the name, then the address, then the TLS files, then the engine control commands).
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

        if ((StartCommandError ?? StopCommandError) is { } commandError)
        {
            error = commandError;
            return false;
        }

        profile = new ConnectionProfile(id, name, kind, host, port, pipeName, tls)
        {
            EngineControl = EngineControlOn ? new EngineControl(StartCommand.Trim(), StopCommand.Trim()) : null,
        };
        return true;
    }

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? CommandError(string text, string missing, string prefix)
    {
        if (string.IsNullOrWhiteSpace(text))
            return missing;
        return CommandLine.Parse(text).Error is { } error ? prefix + error : null;
    }
}

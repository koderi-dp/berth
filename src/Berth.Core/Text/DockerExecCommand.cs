using System.Collections.Immutable;
using Berth.Connections;

namespace Berth.Text;

/// <summary>
/// The <c>docker exec</c> command that opens the same shell as the Exec tab: as arguments for starting docker, and as text to paste.
/// The connection is always named, so the command does not depend on <c>DOCKER_HOST</c> or the CLI's context.
/// </summary>
public static class DockerExecCommand
{
    /// <summary>Why there is no command for a connection docker cannot be pointed at by Berth (a kind it does not know).</summary>
    public const string NotAvailableText = "Not available for this connection.";

    /// <summary>
    /// Said beside the external terminal's actions on an SSH connection: the docker CLI then makes its own SSH connection, with the
    /// user's ssh, agent and known_hosts, and none of what Berth keeps.
    /// </summary>
    public const string SshNote =
        "The docker CLI connects with your own ssh, its agent, keys and known_hosts, not with what Berth keeps. It may ask for the password itself.";

    /// <summary>Why there is no command for a TLS connection with half a client certificate: docker would be told something else.</summary>
    public const string HalfCertificateText = "The connection has a client certificate without its key, or a key without its certificate.";

    /// <summary>Null when docker can be pointed at <paramref name="connection"/>; otherwise why not, in a sentence the UI can show.</summary>
    public static string? Unavailable(ConnectionProfile connection)
    {
        if (connection.Kind is not (TransportKind.Tcp or TransportKind.NamedPipe or TransportKind.Ssh))
            return NotAvailableText;
        if (connection is { Kind: TransportKind.Tcp, Tls: { } tls } && string.IsNullOrEmpty(tls.CertPath) != string.IsNullOrEmpty(tls.KeyPath))
            return HalfCertificateText;
        return null;
    }

    /// <summary>What to say beside the command for <paramref name="connection"/>: <see cref="SshNote"/> over SSH, otherwise null.</summary>
    public static string? Note(ConnectionProfile connection) => connection.Kind == TransportKind.Ssh ? SshNote : null;

    /// <summary>
    /// What follows "docker": the connection's flags (<c>--tlsverify --tlscacert</c>, and <c>--tlscert --tlskey</c> with a client
    /// certificate, then <c>-H</c> and the address), then <c>exec -it</c>, the container and the command.
    /// </summary>
    /// <exception cref="ArgumentException">The connection is <see cref="Unavailable"/>, or the container or command is empty.</exception>
    public static ImmutableArray<string> Arguments(ConnectionProfile connection, string containerId, IReadOnlyList<string> command) =>
        [.. Words(connection, containerId, command).Select(w => w.Text)];

    /// <summary>"docker " and the arguments, each value through <see cref="ShellQuote.Quote"/> and docker's own words bare.</summary>
    /// <exception cref="ArgumentException">As for <see cref="Arguments"/>.</exception>
    public static string Text(ConnectionProfile connection, string containerId, IReadOnlyList<string> command, ShellKind shell) =>
        "docker " + string.Join(' ', Words(connection, containerId, command).Select(w => w.IsValue ? ShellQuote.Quote(w.Text, shell) : w.Text));

    private static List<(string Text, bool IsValue)> Words(ConnectionProfile connection, string containerId, IReadOnlyList<string> command)
    {
        if (Unavailable(connection) is { } why)
            throw new ArgumentException(why, nameof(connection));
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        if (command.Count == 0)
            throw new ArgumentException("A shell needs a command.", nameof(command));

        var words = new List<(string Text, bool IsValue)>();
        void Option(string name) => words.Add((name, false));
        void Value(string value) => words.Add((value, true));

        if (connection.Kind == TransportKind.Tcp && connection.Tls is { } tls)
        {
            Option("--tlsverify");
            Option("--tlscacert");
            Value(tls.CaPath);
            if (tls is { CertPath: { Length: > 0 } cert, KeyPath: { Length: > 0 } key })
            {
                Option("--tlscert");
                Value(cert);
                Option("--tlskey");
                Value(key);
            }
        }

        Option("-H");
        Value(EngineAddress.Format(connection));
        Option("exec");
        Option("-it");
        Value(containerId);
        foreach (var word in command)
            Value(word);
        return words;
    }
}

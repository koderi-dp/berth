using System.Collections.Immutable;
using Berth.Connections;

namespace Berth.Text;

/// <summary>
/// The <c>docker exec</c> command that opens the same shell as the Exec tab: as arguments for starting docker, and as text to paste.
/// The connection is always named, so the command does not depend on <c>DOCKER_HOST</c> or the CLI's context.
/// </summary>
public static class DockerExecCommand
{
    /// <summary>Why there is no command for a connection docker cannot be pointed at by Berth (SSH until phase 7).</summary>
    public const string NotAvailableText = "Not available for this connection.";

    /// <summary>Null when docker can be pointed at <paramref name="connection"/>; otherwise why not, in a sentence the UI can show.</summary>
    public static string? Unavailable(ConnectionProfile connection) =>
        connection.Kind is TransportKind.Tcp or TransportKind.NamedPipe ? null : NotAvailableText;

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
            if (tls.CertPath is { } cert && tls.KeyPath is { } key)
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

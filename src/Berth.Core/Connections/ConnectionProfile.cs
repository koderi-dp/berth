namespace Berth.Connections;

/// <summary>How Berth reaches an engine.</summary>
public enum TransportKind
{
    Tcp,
    NamedPipe,

    /// <summary>An SSH session to a host whose docker CLI reaches the engine (<c>docker system dial-stdio</c>).</summary>
    Ssh,
}

/// <summary>The files a TLS connection uses: the CA that signed the engine's certificate, and an optional client certificate and key.</summary>
public sealed record TlsOptions(string CaPath, string? CertPath, string? KeyPath);

/// <summary>
/// A saved connection. <see cref="Host"/> and <see cref="Port"/> are used for TCP and SSH; <see cref="PipeName"/> for a named pipe, whose
/// <see cref="Host"/> is the pipe's server ("." for this machine) and whose <see cref="Port"/> is 0. An SSH connection has
/// <see cref="Ssh"/>; it never holds a password or passphrase (those are in an <see cref="ISecretStore"/>).
/// </summary>
public sealed record ConnectionProfile(string Id, string Name, TransportKind Kind, string Host, int Port, string PipeName, TlsOptions? Tls)
{
    /// <summary>The commands that start and stop this connection's engine, or null when Berth does not control it.</summary>
    public EngineControl? EngineControl { get; init; }

    /// <summary>The user, the sign-in and the accepted host keys of an SSH connection; null for any other kind.</summary>
    public SshOptions? Ssh { get; init; }
}

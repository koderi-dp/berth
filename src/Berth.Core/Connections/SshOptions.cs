using System.Collections.Immutable;

namespace Berth.Connections;

/// <summary>How Berth signs in to an SSH host: by password (the default) or by a private key file.</summary>
public enum SshSignIn
{
    Password,
    KeyFile,
}

/// <summary>What a host's key is to a connection: accepted before, never seen for that host and port, or not the one accepted.</summary>
public enum HostKeyCheck
{
    Known,
    Unknown,
    Changed,
}

/// <summary>
/// A host key: the host and port it was offered on, its type (the SSH name of the key, such as <c>ssh-ed25519</c>) and its SHA-256
/// fingerprint as <c>ssh-keygen -lf</c> prints it (<c>SHA256:</c> and unpadded base64).
/// </summary>
public sealed record HostKey(string Host, int Port, string Type, string Fingerprint)
{
    /// <summary>
    /// The key type behind a host key algorithm: an RSA key is offered under its signature algorithms (<c>rsa-sha2-512</c>,
    /// <c>rsa-sha2-256</c>), which name the same key as <c>ssh-rsa</c>.
    /// </summary>
    public static string TypeOf(string algorithm) => algorithm is "rsa-sha2-512" or "rsa-sha2-256" ? "ssh-rsa" : algorithm;

    /// <summary>Whether this key is for <paramref name="host"/> and <paramref name="port"/> (host names compare without case).</summary>
    public bool IsFor(string host, int port) => Port == port && string.Equals(Host, host, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An SSH connection's own settings. Equal when every field and every saved key is (the keys compare by value, in order), so an edit
/// that changes nothing leaves the connection's session alone.
/// </summary>
/// <param name="User">The user name to sign in as.</param>
/// <param name="KeyPath">The private key file, for <see cref="SshSignIn.KeyFile"/>; null for a password.</param>
/// <param name="KnownHosts">The host keys the user accepted, at most one for each host and port.</param>
public sealed record SshOptions(string User, SshSignIn SignIn, string? KeyPath, ImmutableArray<HostKey> KnownHosts)
{
    /// <summary>The key saved for <paramref name="host"/> and <paramref name="port"/>, or null.</summary>
    public HostKey? SavedFor(string host, int port) => KnownHosts.FirstOrDefault(key => key.IsFor(host, port));

    /// <summary>
    /// Whether <paramref name="offered"/> is the key saved for its host and port. A key of another type, or another fingerprint, is
    /// <see cref="HostKeyCheck.Changed"/>: a host that starts offering a different key is never trusted without the user.
    /// </summary>
    public HostKeyCheck Check(HostKey offered) =>
        SavedFor(offered.Host, offered.Port) switch
        {
            null => HostKeyCheck.Unknown,
            { } saved when saved.Type == offered.Type && saved.Fingerprint == offered.Fingerprint => HostKeyCheck.Known,
            _ => HostKeyCheck.Changed,
        };

    /// <summary>
    /// <paramref name="key"/> saved for its host and port. Only an unknown key is saved: a key already saved changes nothing, and a
    /// changed key is never put in place of the saved one (the user removes the saved key first, <see cref="Remove"/>). Returns this
    /// instance when nothing changes.
    /// </summary>
    public SshOptions Accept(HostKey key) => Check(key) == HostKeyCheck.Unknown ? this with { KnownHosts = KnownHosts.Add(key) } : this;

    /// <summary>The saved keys without <paramref name="key"/>.</summary>
    public SshOptions Remove(HostKey key) => this with { KnownHosts = KnownHosts.Remove(key) };

    public bool Equals(SshOptions? other) =>
        other is not null
        && User == other.User
        && SignIn == other.SignIn
        && KeyPath == other.KeyPath
        && KnownHosts.SequenceEqual(other.KnownHosts);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(User);
        hash.Add(SignIn);
        hash.Add(KeyPath);
        foreach (var key in KnownHosts)
            hash.Add(key);
        return hash.ToHashCode();
    }
}

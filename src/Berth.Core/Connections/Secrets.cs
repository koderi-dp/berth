namespace Berth.Connections;

/// <summary>The secrets an SSH connection may need: its password, or the passphrase of its key file.</summary>
public enum SecretKind
{
    Password,
    Passphrase,
}

/// <summary>
/// Where an SSH connection's password and passphrase are kept, by connection id and kind: never in <c>settings.json</c>, the log or an
/// exception message. The app's store is Windows Credential Manager; tests use a fake.
/// </summary>
public interface ISecretStore
{
    /// <summary>The secret, or null when none is kept.</summary>
    string? Get(string connectionId, SecretKind kind);

    /// <summary>Keeps <paramref name="secret"/>, in place of any kept before.</summary>
    void Set(string connectionId, SecretKind kind, string secret);

    /// <summary>Forgets the secret; nothing happens when none is kept.</summary>
    void Delete(string connectionId, SecretKind kind);
}

/// <summary>What deleting a connection does to its secrets.</summary>
public static class Secrets
{
    /// <summary>Forgets both secrets of <paramref name="connectionId"/>.</summary>
    public static void DeleteAll(ISecretStore store, string connectionId)
    {
        store.Delete(connectionId, SecretKind.Password);
        store.Delete(connectionId, SecretKind.Passphrase);
    }
}

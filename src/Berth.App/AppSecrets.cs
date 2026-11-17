using Berth.Connections;

namespace Berth;

/// <summary>
/// The secret store the app's SSH connections read: Windows Credential Manager (<see cref="CredentialStore"/>), and a secret the user
/// entered without "Remember", held in memory for the one connection attempt that takes it (<see cref="Offer"/>), then forgotten.
/// A failure to reach Credential Manager is logged in fixed words and the secret treated as not kept (the user is then asked).
/// </summary>
internal sealed class AppSecrets : ISecretStore
{
    private readonly CredentialStore _saved = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<(string, SecretKind), string> _once = [];

    /// <summary>Holds <paramref name="secret"/> for the next attempt of <paramref name="connectionId"/> only.</summary>
    public void Offer(string connectionId, SecretKind kind, string secret)
    {
        lock (_gate)
            _once[(connectionId, kind)] = secret;
    }

    public string? Get(string connectionId, SecretKind kind)
    {
        lock (_gate)
        {
            if (_once.Remove((connectionId, kind), out var offered))
                return offered;
        }

        try
        {
            return _saved.Get(connectionId, kind);
        }
        catch (Exception ex)
        {
            AppLog.Error("reading a secret from Credential Manager failed", ex);
            return null;
        }
    }

    public void Set(string connectionId, SecretKind kind, string secret)
    {
        lock (_gate)
            _once.Remove((connectionId, kind));
        _saved.Set(connectionId, kind, secret);
    }

    public void Delete(string connectionId, SecretKind kind)
    {
        lock (_gate)
            _once.Remove((connectionId, kind));
        try
        {
            _saved.Delete(connectionId, kind);
        }
        catch (Exception ex)
        {
            AppLog.Error("deleting a secret from Credential Manager failed", ex);
        }
    }

    /// <summary>Whether Credential Manager keeps <paramref name="kind"/> for the connection (the editor says so without showing it).</summary>
    public bool IsSaved(string connectionId, SecretKind kind)
    {
        try
        {
            return _saved.Get(connectionId, kind) is not null;
        }
        catch (Exception ex)
        {
            AppLog.Error("reading a secret from Credential Manager failed", ex);
            return false;
        }
    }
}

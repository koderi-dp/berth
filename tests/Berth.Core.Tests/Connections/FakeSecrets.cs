using Berth.Connections;

namespace Berth.Tests;

/// <summary>A secret store in memory that counts its reads, for tests.</summary>
internal sealed class FakeSecrets : ISecretStore
{
    private readonly Dictionary<(string, SecretKind), string> _secrets = [];

    /// <summary>How many times a secret was asked for.</summary>
    public int Reads { get; private set; }

    public string? Get(string connectionId, SecretKind kind)
    {
        Reads++;
        return _secrets.GetValueOrDefault((connectionId, kind));
    }

    public void Set(string connectionId, SecretKind kind, string secret) => _secrets[(connectionId, kind)] = secret;

    public void Delete(string connectionId, SecretKind kind) => _secrets.Remove((connectionId, kind));

    public bool Has(string connectionId, SecretKind kind) => _secrets.ContainsKey((connectionId, kind));
}

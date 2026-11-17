using Berth.Connections;
using Berth.Transport;

namespace Berth.Engine;

/// <summary>The outcome of a connection test. <see cref="Ssh"/> says why an SSH connection failed, so the editor can ask about a host key or a secret.</summary>
public sealed record ConnectionTestResult(bool Ok, string Message, SshProblem? Ssh);

/// <summary>Checks that a connection profile reaches an engine Berth can talk to.</summary>
public static class ConnectionTester
{
    /// <summary>Ok: "Docker 29.9.3, API 1.56", naming the API Berth will speak. Not ok: the error message as the engine or transport gave it.</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var transport = TransportFactory.Create(profile);
        await using var client = new EngineClient(EngineHttp.CreateHandler(transport));
        var result = await TestAsync(client, ct).ConfigureAwait(false);
        return (result.Ok, result.Message);
    }

    /// <summary>As <see cref="TestAsync(ConnectionProfile, CancellationToken)"/>, with an SSH connection's secret read from <paramref name="secrets"/>.</summary>
    public static async Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, ISecretStore secrets, CancellationToken ct)
    {
        await using var transport = TransportFactory.Create(profile, secrets);
        await using var client = new EngineClient(EngineHttp.CreateHandler(transport));
        return await TestAsync(client, ct).ConfigureAwait(false);
    }

    internal static async Task<ConnectionTestResult> TestAsync(EngineClient client, CancellationToken ct)
    {
        try
        {
            var version = await client.ConnectAsync(ct).ConfigureAwait(false);
            return new ConnectionTestResult(true, $"Docker {version.Version}, API {client.Negotiated}", null);
        }
        catch (EngineException ex)
        {
            return new ConnectionTestResult(false, ex.Message, ex.Ssh);
        }
    }
}

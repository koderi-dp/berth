using Berth.Connections;
using Berth.Transport;

namespace Berth.Engine;

/// <summary>Checks that a connection profile reaches an engine Berth can talk to.</summary>
public static class ConnectionTester
{
    /// <summary>Ok: "Docker 29.9.3, API 1.56", naming the API Berth will speak. Not ok: the error message as the engine or transport gave it.</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var transport = TransportFactory.Create(profile);
        await using var client = new EngineClient(EngineHttp.CreateHandler(transport));
        return await TestAsync(client, ct).ConfigureAwait(false);
    }

    internal static async Task<(bool Ok, string Message)> TestAsync(EngineClient client, CancellationToken ct)
    {
        try
        {
            var version = await client.ConnectAsync(ct).ConfigureAwait(false);
            return (true, $"Docker {version.Version}, API {client.Negotiated}");
        }
        catch (EngineException ex)
        {
            return (false, ex.Message);
        }
    }
}

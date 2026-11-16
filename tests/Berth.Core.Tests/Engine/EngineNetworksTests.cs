using System.Net;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class EngineNetworksTests
{
    private const string ShopId = "4d3c2b1a0f9e8d7c6b5a49382716f5e4d3c2b1a0f9e8d7c6b5a49382716f5e4d";

    // The shape of a /networks answer from a 29.9.3 engine, with made-up values: the default bridge, a compose network, and "none"
    // with no IPAM configuration and its optional fields null.
    private const string ThreeNetworks = $$"""
        [
          {
            "Name": "bridge",
            "Id": "b0a1c2d3e4f5061728394a5b6c7d8e9fb0a1c2d3e4f5061728394a5b6c7d8e9f",
            "Created": "2026-11-15T17:55:39.150439993+01:00",
            "Scope": "local",
            "Driver": "bridge",
            "EnableIPv4": true,
            "EnableIPv6": false,
            "IPAM": { "Driver": "default", "Options": null, "Config": [{ "Subnet": "172.18.0.0/16", "Gateway": "172.18.0.1" }] },
            "Internal": false,
            "Attachable": false,
            "Ingress": false,
            "ConfigFrom": { "Network": "" },
            "ConfigOnly": false,
            "Options": { "com.docker.network.bridge.default_bridge": "true" },
            "Labels": {}
          },
          {
            "Name": "shop_default",
            "Id": "{{ShopId}}",
            "Created": "2026-11-15T18:00:00Z",
            "Scope": "local",
            "Driver": "bridge",
            "IPAM": { "Driver": "default", "Config": [{ "Subnet": "172.20.0.0/16" }, { "Subnet": "fd00:20::/64" }] },
            "Internal": true,
            "Labels": { "com.docker.compose.project": "shop", "com.docker.compose.network": "default" }
          },
          {
            "Name": "none",
            "Id": "a0d8942d0f9e8d7c6b5a49382716f5e4d3c2b1a0f9e8d7c6b5a49382716f5e4d",
            "Created": null,
            "Scope": null,
            "Driver": "null",
            "IPAM": { "Driver": "default", "Options": null, "Config": null },
            "Labels": null
          }
        ]
        """;

    private static async Task<(EngineClient Client, StubHandler Stub)> ConnectedAsync(Action<StubHandler> setup)
    {
        var stub = new StubHandler().WithVersion("1.56");
        setup(stub);
        var client = new EngineClient(stub);
        await client.ConnectAsync(CancellationToken.None);
        return (client, stub);
    }

    [Fact]
    public async Task List_maps_each_network()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/networks", ThreeNetworks));
        await using var _client = client;

        var networks = await client.ListNetworksAsync(CancellationToken.None);

        Assert.Equal(("GET", "/v1.56/networks"), stub.Requests[^1]);
        Assert.Equal(3, networks.Count);
        var bridge = networks[0];
        Assert.Equal("b0a1c2d3e4f5061728394a5b6c7d8e9fb0a1c2d3e4f5061728394a5b6c7d8e9f", bridge.Id);
        Assert.Equal("bridge", bridge.Name);
        Assert.Equal("bridge", bridge.Driver);
        Assert.Equal("local", bridge.Scope);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 17, 55, 39, TimeSpan.FromHours(1)).AddTicks(1504399), bridge.Created);
        Assert.False(bridge.Internal);
        Assert.Empty(bridge.Labels);
        Assert.Equal(["172.18.0.0/16"], bridge.Subnets);

        var shop = networks[1];
        Assert.True(shop.Internal);
        Assert.Equal("shop", shop.Labels["com.docker.compose.project"]);
        Assert.Equal(["172.20.0.0/16", "fd00:20::/64"], shop.Subnets);
    }

    [Fact]
    public async Task List_tolerates_null_fields()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/networks", ThreeNetworks));
        await using var _client = client;

        var none = (await client.ListNetworksAsync(CancellationToken.None))[2];

        Assert.Equal("none", none.Name);
        Assert.Null(none.Created);
        Assert.Equal("", none.Scope);
        Assert.Empty(none.Labels);
        Assert.Empty(none.Subnets);
    }

    [Fact]
    public async Task Remove_deletes_the_network()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On($"/v1.56/networks/{ShopId}", HttpStatusCode.NoContent, ""));
        await using var _client = client;

        await client.RemoveNetworkAsync(ShopId, CancellationToken.None);

        Assert.Equal(("DELETE", $"/v1.56/networks/{ShopId}"), stub.Requests[^1]);
    }

    [Theory]
    [InlineData(403, "bridge is a pre-defined network and cannot be removed")]
    [InlineData(409, "error while removing network: network shop_default has active endpoints (name:\\\"shop-web-1\\\" id:\\\"1f0c2a9e8b7d\\\")")]
    public async Task Remove_carries_the_engines_message(int status, string message)
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/networks/abc", (HttpStatusCode)status, $$"""{"message":"{{message}}"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.RemoveNetworkAsync("abc", CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(message.Replace("\\\"", "\"", StringComparison.Ordinal), error.Message);
    }
}

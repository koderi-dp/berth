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

    // A network's inspect answer with two containers attached.
    private const string ShopInspect = $$"""
        {
          "Name": "shop_default",
          "Id": "{{ShopId}}",
          "Created": "2026-11-15T18:00:00Z",
          "Scope": "local",
          "Driver": "bridge",
          "IPAM": { "Driver": "default", "Config": [{ "Subnet": "172.20.0.0/16", "Gateway": "172.20.0.1" }] },
          "Internal": false,
          "Labels": {},
          "Containers": {
            "1f0c2a9e8b7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3": {
              "Name": "shop-web-1",
              "EndpointID": "3b49a3e1770681d577ccb716b81aacedae73a769fe9ed0e6126bece9aa92590b",
              "MacAddress": "92:19:16:1b:af:d4",
              "IPv4Address": "172.20.0.2/16",
              "IPv6Address": ""
            },
            "9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f1e0d9c8b7a6f5e4d3c2b1a0f9e8d": {
              "Name": "shop-db-1",
              "IPv4Address": "172.20.0.3/16"
            }
          },
          "Status": { "IPAM": { "Subnets": { "172.20.0.0/16": { "IPsInUse": 5, "DynamicIPsAvailable": 65531 } } } }
        }
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
    public async Task Inspect_lists_the_attached_containers()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On($"/v1.56/networks/{ShopId}", ShopInspect));
        await using var _client = client;

        var details = await client.InspectNetworkAsync(ShopId, CancellationToken.None);

        Assert.Equal(("GET", $"/v1.56/networks/{ShopId}"), stub.Requests[^1]);
        Assert.Equal("shop_default", details.Network.Name);
        Assert.Equal(["172.20.0.0/16"], details.Network.Subnets);
        Assert.Equal(
            [
                new NetworkEndpoint("1f0c2a9e8b7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3", "shop-web-1", "172.20.0.2/16", "", "92:19:16:1b:af:d4"),
                new NetworkEndpoint("9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f1e0d9c8b7a6f5e4d3c2b1a0f9e8d", "shop-db-1", "172.20.0.3/16", "", ""),
            ],
            details.Containers);
    }

    [Fact]
    public async Task Inspect_tolerates_no_containers()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/networks/abc", """{"Name":"lonely","Id":"abc","Containers":null}"""));
        await using var _client = client;

        var details = await client.InspectNetworkAsync("abc", CancellationToken.None);

        Assert.Equal("lonely", details.Network.Name);
        Assert.Empty(details.Containers);
    }

    [Fact]
    public async Task Inspect_of_a_missing_network_carries_the_engines_404()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/networks/berth-check-nope", HttpStatusCode.NotFound, """{"message":"network berth-check-nope not found"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.InspectNetworkAsync("berth-check-nope", CancellationToken.None));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("network berth-check-nope not found", error.Message);
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

    [Fact]
    public async Task Prune_lists_the_removed_networks()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/networks/prune", """{"NetworksDeleted":["shop_default","berth-it-net"]}"""));
        await using var _client = client;

        var result = await client.PruneNetworksAsync(CancellationToken.None);

        Assert.Equal(("POST", "/v1.56/networks/prune"), stub.Requests[^1]);
        Assert.Equal(["shop_default", "berth-it-net"], result.Deleted);
        Assert.Equal(0, result.SpaceReclaimed);
    }

    [Fact]
    public async Task Prune_tolerates_a_null_list()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/networks/prune", """{"NetworksDeleted":null}"""));
        await using var _client = client;

        Assert.Empty((await client.PruneNetworksAsync(CancellationToken.None)).Deleted);
    }
}

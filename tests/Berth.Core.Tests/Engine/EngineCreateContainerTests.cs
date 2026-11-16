using System.Net;
using System.Text.Json.Nodes;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class EngineCreateContainerTests
{
    private const string Created = """{"Id":"7a6b5c4d3e2f1a0b9c8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b","Warnings":[]}""";

    private static async Task<(EngineClient Client, StubHandler Stub)> ConnectedAsync(Action<StubHandler> setup)
    {
        var stub = new StubHandler().WithVersion("1.56");
        setup(stub);
        var client = new EngineClient(stub);
        await client.ConnectAsync(CancellationToken.None);
        return (client, stub);
    }

    [Fact]
    public async Task Create_sends_image_env_cmd_ports_and_binds_and_returns_the_id()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/containers/create?name=berth-it-web", HttpStatusCode.Created, Created));
        await using var _client = client;
        var request = new ContainerCreateRequest(
            "nginx:1.29",
            "berth-it-web",
            ["MODE=dev", "GREETING=hello world"],
            [
                new PortBinding(80, "tcp", null, 8080),
                new PortBinding(80, "tcp", "127.0.0.1", 8081),
                new PortBinding(53, "udp", null, null),
            ],
            ["shop_data:/data", "/srv/site:/usr/share/nginx/html:ro"],
            ["nginx", "-g", "daemon off;"]);

        var id = await client.CreateContainerAsync(request, CancellationToken.None);

        Assert.Equal("7a6b5c4d3e2f1a0b9c8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b", id);
        Assert.Equal(("POST", "/v1.56/containers/create?name=berth-it-web"), stub.Requests[^1]);
        AssertJson(
            """
            {
              "Image": "nginx:1.29",
              "Env": ["MODE=dev", "GREETING=hello world"],
              "Cmd": ["nginx", "-g", "daemon off;"],
              "ExposedPorts": { "80/tcp": {}, "53/udp": {} },
              "HostConfig": {
                "Binds": ["shop_data:/data", "/srv/site:/usr/share/nginx/html:ro"],
                "PortBindings": {
                  "80/tcp": [{ "HostIp": "", "HostPort": "8080" }, { "HostIp": "127.0.0.1", "HostPort": "8081" }],
                  "53/udp": [{ "HostIp": "", "HostPort": "" }]
                }
              }
            }
            """,
            stub.Bodies[^1]);
    }

    [Fact]
    public async Task Create_without_a_name_or_command_lets_the_engine_and_the_image_decide()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/containers/create", HttpStatusCode.Created, Created));
        await using var _client = client;

        await client.CreateContainerAsync(new ContainerCreateRequest("alpine", null, [], [], []), CancellationToken.None);

        Assert.Equal(("POST", "/v1.56/containers/create"), stub.Requests[^1]);
        AssertJson(
            """{ "Image": "alpine", "Env": [], "ExposedPorts": {}, "HostConfig": { "Binds": [], "PortBindings": {} } }""",
            stub.Bodies[^1]);
    }

    [Fact]
    public async Task Create_escapes_the_name()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/containers/create?name=berth-it-a%26b", HttpStatusCode.Created, Created));
        await using var _client = client;

        await client.CreateContainerAsync(new ContainerCreateRequest("alpine", "berth-it-a&b", [], [], []), CancellationToken.None);

        Assert.Equal(("POST", "/v1.56/containers/create?name=berth-it-a%26b"), stub.Requests[^1]);
    }

    [Theory]
    [InlineData(404, "No such image: berth-check-nope:latest")]
    [InlineData(409, "Conflict. The container name \\\"/berth-it-web\\\" is already in use by container \\\"7a6b5c4d3e2f\\\". You have to remove (or rename) that container to be able to reuse that name.")]
    public async Task Create_carries_the_engines_message(int status, string message)
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/containers/create?name=berth-it-web", (HttpStatusCode)status, $$"""{"message":"{{message}}"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(
            () => client.CreateContainerAsync(new ContainerCreateRequest("alpine", "berth-it-web", [], [], []), CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(message.Replace("\\\"", "\"", StringComparison.Ordinal), error.Message);
    }

    private static void AssertJson(string expected, string actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"Expected {JsonNode.Parse(expected)!.ToJsonString()}\nbut got {actual}");
}

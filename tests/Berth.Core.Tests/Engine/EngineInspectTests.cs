using System.Net;
using System.Text.Json.Nodes;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class EngineInspectTests
{
    // A /containers/{id}/json answer from a 29.9.3 engine, trimmed and with made-up values. It keeps a few fields Berth does not
    // read (ResolvConfPath, Driver, HostConfig.ShmSize) so the raw view can be checked for losing nothing.
    private const string WebInspect = """
        {
          "Id": "1f0c2a9e8b7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3",
          "Created": "2026-11-15T21:29:56.352716446Z",
          "Path": "/docker-entrypoint.sh",
          "Args": ["nginx", "-g", "daemon off;"],
          "State": {
            "Status": "running",
            "Running": true,
            "Paused": false,
            "Restarting": false,
            "OOMKilled": false,
            "Dead": false,
            "Pid": 4242,
            "ExitCode": 0,
            "Error": "",
            "StartedAt": "2026-11-15T21:36:22.030409933Z",
            "FinishedAt": "2026-11-15T21:36:09.2Z",
            "Health": { "Status": "healthy", "FailingStreak": 0, "Log": [] }
          },
          "Image": "sha256:0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9",
          "ResolvConfPath": "/var/lib/docker/containers/1f0c2a9e/resolv.conf",
          "Name": "/shop-web-1",
          "RestartCount": 0,
          "Driver": "overlayfs",
          "Platform": "linux",
          "HostConfig": {
            "RestartPolicy": { "Name": "unless-stopped", "MaximumRetryCount": 0 },
            "ShmSize": 67108864
          },
          "Mounts": [
            {
              "Type": "bind",
              "Source": "/home/someone/site",
              "Destination": "/usr/share/nginx/html",
              "Mode": "ro",
              "RW": false,
              "Propagation": "rprivate"
            },
            {
              "Type": "volume",
              "Name": "shop_cache",
              "Source": "/var/lib/docker/volumes/shop_cache/_data",
              "Destination": "/var/cache/nginx",
              "Driver": "local",
              "Mode": "z",
              "RW": true,
              "Propagation": ""
            }
          ],
          "Config": {
            "Hostname": "1f0c2a9e8b7d",
            "User": "nginx",
            "Tty": false,
            "Env": ["PATH=/usr/local/bin:/usr/bin:/bin", "GREETING=hello"],
            "Cmd": ["nginx", "-g", "daemon off;"],
            "Image": "nginx:1.29",
            "WorkingDir": "/srv",
            "Entrypoint": ["/docker-entrypoint.sh"],
            "Labels": {
              "com.docker.compose.project": "shop",
              "maintainer": "Someone <someone@example.com>"
            }
          },
          "NetworkSettings": {
            "Ports": {
              "80/tcp": [
                { "HostIp": "0.0.0.0", "HostPort": "8080" },
                { "HostIp": "::", "HostPort": "8080" }
              ],
              "443/tcp": null,
              "53/udp": []
            },
            "Networks": {
              "shop_default": {
                "Aliases": ["shop-web-1", "web"],
                "Gateway": "172.30.0.1",
                "IPAddress": "172.30.0.5",
                "MacAddress": "02:42:ac:1e:00:05"
              }
            }
          }
        }
        """;

    // A container that has never run, from an engine that leaves out or nulls every optional section.
    private const string BareInspect = """
        {
          "Id": "abc",
          "Created": "2026-11-15T08:00:00Z",
          "Path": "sh",
          "Args": null,
          "State": {
            "Status": "created",
            "Running": false,
            "Paused": false,
            "Restarting": false,
            "ExitCode": 0,
            "Error": "",
            "StartedAt": "0001-01-01T00:00:00Z",
            "FinishedAt": "0001-01-01T00:00:00Z"
          },
          "Image": "sha256:1",
          "Name": "/bare",
          "Config": {
            "Env": null,
            "Cmd": null,
            "Entrypoint": null,
            "Labels": null
          }
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
    public async Task Inspect_requests_the_container()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/containers/abc/json", BareInspect));
        await using var _client = client;

        await client.InspectContainerAsync("abc", CancellationToken.None);

        Assert.Equal(("GET", "/v1.56/containers/abc/json"), stub.Requests[^1]);
    }

    [Fact]
    public async Task Inspect_maps_state_mounts_ports_networks_and_config()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/containers/web/json", WebInspect));
        await using var _client = client;

        var details = (await client.InspectContainerAsync("web", CancellationToken.None)).Details;

        Assert.Equal("1f0c2a9e8b7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3", details.Id);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 21, 29, 56, TimeSpan.Zero).AddTicks(3527164), details.Created);
        Assert.Equal("/docker-entrypoint.sh", details.Path);
        Assert.Equal(["nginx", "-g", "daemon off;"], details.Args);
        Assert.Equal(
            new ContainerStateDetails(
                "running", Running: true, Paused: false, Restarting: false, ExitCode: 0, Error: "",
                StartedAt: new DateTimeOffset(2026, 11, 15, 21, 36, 22, TimeSpan.Zero).AddTicks(304099),
                FinishedAt: new DateTimeOffset(2026, 11, 15, 21, 36, 9, 200, TimeSpan.Zero),
                Health: "healthy"),
            details.State);
        Assert.Equal("sha256:0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9", details.ImageId);
        Assert.Equal("nginx:1.29", details.Image);
        Assert.Equal(["PATH=/usr/local/bin:/usr/bin:/bin", "GREETING=hello"], details.Env);
        Assert.Equal(["nginx", "-g", "daemon off;"], details.Cmd);
        Assert.Equal(["/docker-entrypoint.sh"], details.Entrypoint);
        Assert.Equal("/srv", details.WorkingDir);
        Assert.Equal("nginx", details.User);
        Assert.False(details.Tty);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["com.docker.compose.project"] = "shop",
                ["maintainer"] = "Someone <someone@example.com>",
            },
            details.Labels);
        Assert.Equal(
            [
                new MountDetails("bind", "/home/someone/site", "/usr/share/nginx/html", "ro", ReadWrite: false, Name: null),
                new MountDetails("volume", "/var/lib/docker/volumes/shop_cache/_data", "/var/cache/nginx", "z", ReadWrite: true, Name: "shop_cache"),
            ],
            details.Mounts);
        Assert.Equal(
            [
                new PortMapping("0.0.0.0", 80, 8080, "tcp"),
                new PortMapping("::", 80, 8080, "tcp"),
                new PortMapping(null, 443, null, "tcp"),
                new PortMapping(null, 53, null, "udp"),
            ],
            details.Ports);
        Assert.Equal([new NetworkAttachment("shop_default", "172.30.0.5", "172.30.0.1", "02:42:ac:1e:00:05")], details.Networks);
        Assert.Equal("unless-stopped", details.RestartPolicy);
        Assert.Equal("linux", details.Platform);
    }

    [Fact]
    public async Task Inspect_tolerates_missing_and_null_sections()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/containers/abc/json", BareInspect));
        await using var _client = client;

        var details = (await client.InspectContainerAsync("abc", CancellationToken.None)).Details;

        Assert.Equal("abc", details.Id);
        Assert.Equal("bare", details.Name);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero), details.Created);
        Assert.Empty(details.Args);
        Assert.Equal(
            new ContainerStateDetails("created", false, false, false, 0, "", StartedAt: null, FinishedAt: null, Health: null),
            details.State);
        Assert.Equal("sha256:1", details.ImageId);
        Assert.Equal("", details.Image);
        Assert.Empty(details.Env);
        Assert.Empty(details.Cmd);
        Assert.Empty(details.Entrypoint);
        Assert.Equal("", details.WorkingDir);
        Assert.Equal("", details.User);
        Assert.False(details.Tty);
        Assert.Empty(details.Labels);
        Assert.Empty(details.Mounts);
        Assert.Empty(details.Ports);
        Assert.Empty(details.Networks);
        Assert.Equal("", details.RestartPolicy);
        Assert.Equal("", details.Platform);
    }

    [Fact]
    public async Task Raw_json_is_the_engines_answer_indented_with_nothing_removed()
    {
        // The engine sends one compact line; the raw view gets the same document laid out two spaces per level.
        var compact = JsonNode.Parse(WebInspect)!.ToJsonString();
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/containers/web/json", compact));
        await using var _client = client;

        var raw = (await client.InspectContainerAsync("web", CancellationToken.None)).RawJson;

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(WebInspect), JsonNode.Parse(raw)));
        Assert.StartsWith("{\n  \"Id\": \"1f0c2a9e", raw);
        Assert.Contains("\n  \"ResolvConfPath\": \"/var/lib/docker/containers/1f0c2a9e/resolv.conf\",", raw);
        Assert.Contains("\n    \"ShmSize\": 67108864", raw);
        Assert.DoesNotContain("\r", raw);
        Assert.Contains("\"maintainer\": \"Someone <someone@example.com>\"", raw);
    }

    [Fact]
    public async Task A_missing_container_throws_the_engines_404_message()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/containers/gone/json",
            HttpStatusCode.NotFound,
            """{"message":"No such container: gone"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.InspectContainerAsync("gone", CancellationToken.None));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("No such container: gone", error.Message);
    }

    [Fact]
    public async Task Name_drops_the_leading_slash()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/containers/web/json", WebInspect));
        await using var _client = client;

        var details = (await client.InspectContainerAsync("web", CancellationToken.None)).Details;

        Assert.Equal("shop-web-1", details.Name);
    }
}

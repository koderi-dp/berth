using System.Net;
using System.Net.Sockets;
using Berth.Connections;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class EngineClientTests
{
    // A recorded /containers/json answer from a 29.9.3 engine, trimmed to the fields Berth reads plus a few it ignores, with neutral names.
    private const string TwoContainers = """
        [
          {
            "Id": "1f0c2a9e8b7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3",
            "Names": ["/shop-web-1"],
            "Image": "nginx:1.29",
            "ImageID": "sha256:0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9",
            "ImageManifestDescriptor": { "mediaType": "application/vnd.oci.image.manifest.v1+json", "size": 2775 },
            "Command": "/docker-entrypoint.sh nginx -g 'daemon off;'",
            "Created": 1794768627,
            "Ports": [
              { "IP": "0.0.0.0", "PrivatePort": 80, "PublicPort": 8080, "Type": "tcp" },
              { "IP": "::", "PrivatePort": 80, "PublicPort": 8080, "Type": "tcp" },
              { "PrivatePort": 443, "Type": "tcp" }
            ],
            "Labels": {
              "com.docker.compose.project": "shop",
              "com.docker.compose.service": "web"
            },
            "State": "running",
            "Status": "Up 12 minutes",
            "HostConfig": { "NetworkMode": "shop_default" },
            "Health": { "Status": "none", "FailingStreak": 0 },
            "Mounts": []
          },
          {
            "Id": "9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f1e0d9c8b7a6f5e4d3c2b1a0f9e8d",
            "Names": ["/scratch-db"],
            "Image": "postgres:18",
            "ImageID": "sha256:f9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f1e0d9c8b7a6f5e4d3c2b1a0f9e8",
            "Command": "docker-entrypoint.sh postgres",
            "Created": 1794700000,
            "Ports": [],
            "Labels": {},
            "State": "exited",
            "Status": "Exited (0) 2 hours ago"
          }
        ]
        """;

    private static async Task<(EngineClient Client, StubHandler Stub)> ConnectedAsync(Action<StubHandler>? setup = null)
    {
        var stub = new StubHandler().WithVersion("1.56");
        setup?.Invoke(stub);
        var client = new EngineClient(stub);
        await client.ConnectAsync(CancellationToken.None);
        return (client, stub);
    }

    [Fact]
    public async Task Connect_reads_the_engine_version()
    {
        await using var client = new EngineClient(new StubHandler().WithVersion("1.56"));

        var version = await client.ConnectAsync(CancellationToken.None);

        Assert.Equal(new EngineVersion("29.9.3", "1.56", "1.40", "linux", "amd64"), version);
        Assert.Equal(new ApiVersion(1, 56), client.Negotiated);
    }

    [Fact]
    public async Task Connect_uses_the_engine_version_when_it_is_older_than_ours()
    {
        var stub = new StubHandler().WithVersion("1.50").On("/v1.50/containers/json?all=true", "[]");
        await using var client = new EngineClient(stub);

        await client.ConnectAsync(CancellationToken.None);
        await client.ListContainersAsync(CancellationToken.None);

        Assert.Equal(new ApiVersion(1, 50), client.Negotiated);
        Assert.Equal("/v1.50/containers/json?all=true", stub.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Connect_caps_at_1_56_when_the_engine_is_newer()
    {
        var stub = new StubHandler().WithVersion("1.60").On("/v1.56/containers/json?all=true", "[]");
        await using var client = new EngineClient(stub);

        await client.ConnectAsync(CancellationToken.None);
        await client.ListContainersAsync(CancellationToken.None);

        Assert.Equal(ApiVersion.Maximum, client.Negotiated);
        Assert.Equal("/v1.56/containers/json?all=true", stub.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Connect_refuses_an_engine_below_1_44()
    {
        await using var client = new EngineClient(new StubHandler().WithVersion("1.41"));

        var error = await Assert.ThrowsAsync<EngineException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Equal("This engine speaks API 1.41. Berth needs 1.44 or newer (Docker 25).", error.Message);
        Assert.Null(client.Negotiated);
    }

    [Fact]
    public async Task Info_maps_the_engines_fields()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/info",
            """{"ID":"x","Name":"build-host","ServerVersion":"29.9.3","NCPU":16,"MemTotal":33554432000,"Containers":3,"ContainersRunning":2,"ContainersPaused":0,"ContainersStopped":1,"Images":75,"Driver":"overlayfs","OperatingSystem":"Ubuntu 24.04.5 LTS","KernelVersion":"6.18.58.1-microsoft-standard-WSL2"}"""));
        await using var _client = client;

        var info = await client.GetInfoAsync(CancellationToken.None);

        Assert.Equal(new EngineInfo("build-host", "29.9.3", 16, 33554432000, 2, 0, 1, 75) { OperatingSystem = "Ubuntu 24.04.5 LTS", KernelVersion = "6.18.58.1-microsoft-standard-WSL2" }, info);
    }

    [Fact]
    public async Task List_requests_all_containers()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/containers/json?all=true", "[]"));
        await using var _client = client;

        var containers = await client.ListContainersAsync(CancellationToken.None);

        Assert.Empty(containers);
        Assert.Equal(("GET", "/v1.56/containers/json?all=true"), stub.Requests[^1]);
    }

    [Fact]
    public async Task List_parses_names_ports_and_labels()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/containers/json?all=true", TwoContainers));
        await using var _client = client;

        var containers = await client.ListContainersAsync(CancellationToken.None);

        Assert.Equal(2, containers.Count);
        var web = containers[0];
        Assert.Equal("1f0c2a9e8b7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3", web.Id);
        Assert.Equal(["/shop-web-1"], web.Names);
        Assert.Equal("nginx:1.29", web.Image);
        Assert.Equal("sha256:0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9", web.ImageId);
        Assert.Equal("/docker-entrypoint.sh nginx -g 'daemon off;'", web.Command);
        Assert.Equal(1794768627, web.Created);
        Assert.Equal("running", web.State);
        Assert.Equal("Up 12 minutes", web.Status);
        Assert.Equal(
            [new PortMapping("0.0.0.0", 80, 8080, "tcp"), new PortMapping("::", 80, 8080, "tcp"), new PortMapping(null, 443, null, "tcp")],
            web.Ports);
        Assert.Equal("shop", web.Labels["com.docker.compose.project"]);
        Assert.Equal("web", web.Labels["com.docker.compose.service"]);

        var db = containers[1];
        Assert.Equal(["/scratch-db"], db.Names);
        Assert.Equal("exited", db.State);
        Assert.Empty(db.Ports);
        Assert.Empty(db.Labels);
    }

    [Fact]
    public async Task List_tolerates_missing_ports_and_null_labels()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/containers/json?all=true",
            """[{"Id":"abc","Names":null,"Image":"busybox","ImageID":"sha256:1","Command":"sh","Created":1,"State":"created","Status":"Created","Labels":null}]"""));
        await using var _client = client;

        var containers = await client.ListContainersAsync(CancellationToken.None);

        var only = Assert.Single(containers);
        Assert.Equal("abc", only.Id);
        Assert.Empty(only.Names);
        Assert.Empty(only.Ports);
        Assert.Empty(only.Labels);
        Assert.Empty(only.Mounts);
        Assert.Empty(only.Networks);
    }

    [Fact]
    public async Task List_parses_mounts_and_network_names()
    {
        // The Mounts and NetworkSettings of a 29.9.3 engine's /containers/json entry, with made-up values; the third mount and the
        // second network carry nulls.
        const string answer = """
            [
              {
                "Id": "abc", "Names": ["/shop-web-1"], "Image": "nginx:1.29", "State": "running", "Status": "Up 1 minute",
                "Mounts": [
                  { "Type": "volume", "Name": "shop_data", "Source": "/var/lib/docker/volumes/shop_data/_data", "Destination": "/data",
                    "Driver": "local", "Mode": "z", "RW": true, "Propagation": "" },
                  { "Type": "bind", "Source": "/srv/site", "Destination": "/usr/share/nginx/html", "Mode": "ro", "RW": false,
                    "Propagation": "rprivate" },
                  { "Type": "tmpfs", "Name": null, "Source": null, "Destination": "/run", "Mode": null }
                ],
                "NetworkSettings": {
                  "Networks": {
                    "shop_default": { "NetworkID": "4d3c2b1a", "EndpointID": "3b49a3e1", "Gateway": "172.20.0.1", "IPAddress": "172.20.0.2" },
                    "monitoring": null
                  }
                }
              }
            ]
            """;
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/containers/json?all=true", answer));
        await using var _client = client;

        var web = Assert.Single(await client.ListContainersAsync(CancellationToken.None));

        Assert.Equal(
            [
                new MountDetails("volume", "/var/lib/docker/volumes/shop_data/_data", "/data", "z", true, "shop_data"),
                new MountDetails("bind", "/srv/site", "/usr/share/nginx/html", "ro", false, null),
                new MountDetails("tmpfs", "", "/run", "", false, null),
            ],
            web.Mounts);
        Assert.Equal(["shop_default", "monitoring"], web.Networks);
    }

    [Theory]
    [InlineData("start", "POST", "/v1.56/containers/abc/start")]
    [InlineData("stop", "POST", "/v1.56/containers/abc/stop")]
    [InlineData("restart", "POST", "/v1.56/containers/abc/restart")]
    [InlineData("pause", "POST", "/v1.56/containers/abc/pause")]
    [InlineData("unpause", "POST", "/v1.56/containers/abc/unpause")]
    public async Task Actions_call_the_engine(string action, string method, string path)
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On(path, HttpStatusCode.NoContent, ""));
        await using var _client = client;

        await RunAsync(client, action, "abc");

        Assert.Equal((method, path), stub.Requests[^1]);
    }

    [Fact]
    public async Task Remove_sends_force_when_asked()
    {
        var (client, stub) = await ConnectedAsync(stub => stub
            .On("/v1.56/containers/abc?force=true", HttpStatusCode.NoContent, "")
            .On("/v1.56/containers/abc", HttpStatusCode.NoContent, ""));
        await using var _client = client;

        await client.RemoveContainerAsync("abc", force: true, CancellationToken.None);
        Assert.Equal(("DELETE", "/v1.56/containers/abc?force=true"), stub.Requests[^1]);

        await client.RemoveContainerAsync("abc", force: false, CancellationToken.None);
        Assert.Equal(("DELETE", "/v1.56/containers/abc"), stub.Requests[^1]);
    }

    [Fact]
    public async Task A_304_from_start_or_stop_is_success()
    {
        var (client, _) = await ConnectedAsync(stub => stub
            .On("/v1.56/containers/abc/start", HttpStatusCode.NotModified, "")
            .On("/v1.56/containers/abc/stop", HttpStatusCode.NotModified, ""));
        await using var _client = client;

        await client.StartContainerAsync("abc", CancellationToken.None);
        await client.StopContainerAsync("abc", CancellationToken.None);
    }

    [Fact]
    public async Task An_error_carries_the_engines_own_message()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/containers/abc",
            HttpStatusCode.Conflict,
            """{"message":"conflict: container is running"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(
            () => client.RemoveContainerAsync("abc", force: false, CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("conflict: container is running", error.Message);
    }

    [Fact]
    public async Task An_error_without_json_uses_the_status_line()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/info", HttpStatusCode.BadGateway, "Bad Gateway"));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.GetInfoAsync(CancellationToken.None));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("502 Bad Gateway", error.Message);
    }

    [Fact]
    public async Task A_call_before_Connect_throws_InvalidOperationException()
    {
        var stub = new StubHandler();
        await using var client = new EngineClient(stub);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListContainersAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartContainerAsync("abc", CancellationToken.None));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task A_transport_failure_carries_the_transports_message()
    {
        var refused = new SocketException((int)SocketError.ConnectionRefused);
        await using var client = new EngineClient(new ThrowingHandler(new HttpRequestException("wrapped", refused)));

        var error = await Assert.ThrowsAsync<EngineException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Equal(0, error.StatusCode);
        Assert.Equal(refused.Message, error.Message);
    }

    [Fact]
    public async Task Cancelling_the_caller_token_throws_OperationCanceledException()
    {
        await using var client = new EngineClient(new StubHandler().WithVersion("1.56"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(cancelled.Token));
    }

    [Fact]
    public async Task A_transport_failure_after_the_caller_cancels_throws_OperationCanceledException()
    {
        using var cancel = new CancellationTokenSource();
        var refused = new HttpRequestException("wrapped", new SocketException((int)SocketError.ConnectionReset));
        await using var client = new EngineClient(new ThrowingHandler(refused, beforeThrow: cancel.Cancel));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(cancel.Token));
    }

    [Fact]
    public async Task Test_connection_to_a_closed_port_is_not_ok()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var profile = new ConnectionProfile("id", "closed", TransportKind.Tcp, "127.0.0.1", port, "", Tls: null);

        var (ok, message) = await ConnectionTester.TestAsync(profile, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(new SocketException((int)SocketError.ConnectionRefused).Message, message);
    }

    [Fact]
    public async Task Test_connection_message_names_the_engine_and_api_version()
    {
        var stub = new StubHandler().WithVersion("1.60");
        await using var client = new EngineClient(stub);

        var (ok, message) = await ConnectionTester.TestAsync(client, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("Docker 29.9.3, API 1.56", message);
    }

    private static Task RunAsync(EngineClient client, string action, string id) => action switch
    {
        "start" => client.StartContainerAsync(id, CancellationToken.None),
        "stop" => client.StopContainerAsync(id, CancellationToken.None),
        "restart" => client.RestartContainerAsync(id, CancellationToken.None),
        "pause" => client.PauseContainerAsync(id, CancellationToken.None),
        "unpause" => client.UnpauseContainerAsync(id, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>Fails every request with <paramref name="error"/>, first running <paramref name="beforeThrow"/> when given.</summary>
    private sealed class ThrowingHandler(Exception error, Action? beforeThrow = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            beforeThrow?.Invoke();
            return Task.FromException<HttpResponseMessage>(error);
        }
    }
}

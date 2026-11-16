using System.Net;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class EngineImagesTests
{
    private const string AlpineId = "sha256:5b1f0e6c2d9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6f5a4b3c";
    private const string DanglingId = "sha256:c3b4a5f6e7d8c9b0a1f2e3d4c5b6a7f8e9d0c1b2a3f4e5d6c7b8a9f0e1d2c3b4";

    // The shape of a /images/json answer from a 29.9.3 engine, with made-up values: a tagged image, an untagged one as older
    // engines report it (placeholders, -1 containers), and one with its lists null and its count missing.
    private const string ThreeImages = $$"""
        [
          {
            "Containers": 2,
            "Created": 1794700000,
            "Id": "{{AlpineId}}",
            "Labels": { "org.opencontainers.image.version": "3.19" },
            "ParentId": "",
            "Descriptor": { "mediaType": "application/vnd.oci.image.index.v1+json", "digest": "{{AlpineId}}", "size": 9218 },
            "RepoDigests": ["alpine@{{AlpineId}}"],
            "RepoTags": ["alpine:3.19", "alpine:latest"],
            "SharedSize": -1,
            "Size": 8800000
          },
          {
            "Containers": -1,
            "Created": 1794600000,
            "Id": "{{DanglingId}}",
            "Labels": null,
            "ParentId": "",
            "RepoDigests": ["<none>@<none>"],
            "RepoTags": ["<none>:<none>"],
            "SharedSize": -1,
            "Size": 1234
          },
          { "Id": "sha256:1", "RepoTags": null, "RepoDigests": null, "Created": 1, "Size": 2 }
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
    public async Task List_maps_each_image()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/images/json", ThreeImages));
        await using var _client = client;

        var images = await client.ListImagesAsync(CancellationToken.None);

        Assert.Equal(("GET", "/v1.56/images/json"), stub.Requests[^1]);
        Assert.Equal(3, images.Count);
        var alpine = images[0];
        Assert.Equal(AlpineId, alpine.Id);
        Assert.Equal(["alpine:3.19", "alpine:latest"], alpine.RepoTags);
        Assert.Equal([$"alpine@{AlpineId}"], alpine.RepoDigests);
        Assert.Equal(1794700000, alpine.Created);
        Assert.Equal(8800000, alpine.Size);
        Assert.Equal(2, alpine.Containers);
    }

    [Fact]
    public async Task List_drops_the_placeholders_of_an_untagged_image_and_an_uncounted_container_total()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/images/json", ThreeImages));
        await using var _client = client;

        var images = await client.ListImagesAsync(CancellationToken.None);

        Assert.Equal(DanglingId, images[1].Id);
        Assert.Null(images[1].Containers);
        Assert.Empty(images[1].RepoTags);
        Assert.Empty(images[1].RepoDigests);
        Assert.Empty(images[2].RepoTags);
        Assert.Empty(images[2].RepoDigests);
        Assert.Null(images[2].Containers);
    }

    // An image's inspect answer, the parts Berth reads plus some it ignores, with made-up values.
    private const string NginxInspect = """
        {
          "Id": "sha256:0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9",
          "RepoTags": ["nginx:1.29"],
          "RepoDigests": ["nginx@sha256:9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3c2b1a0"],
          "Comment": "buildkit.dockerfile.v0",
          "Created": "2026-11-05T18:00:28.583714131Z",
          "Config": {
            "ExposedPorts": { "80/tcp": {}, "443/tcp": {} },
            "Env": ["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "NGINX_VERSION=1.29.0"],
            "Entrypoint": ["/docker-entrypoint.sh"],
            "Cmd": ["nginx", "-g", "daemon off;"],
            "WorkingDir": "/",
            "Labels": { "maintainer": "someone@example.com" },
            "StopSignal": "SIGQUIT"
          },
          "Architecture": "amd64",
          "Os": "linux",
          "Size": 192000000,
          "RootFS": { "Type": "layers", "Layers": ["sha256:1", "sha256:2"] },
          "Metadata": { "LastTagTime": "2026-11-15T16:54:18.375585919Z" }
        }
        """;

    [Fact]
    public async Task Inspect_maps_ports_env_cmd_labels_size_created_and_platform()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/images/nginx%3A1.29/json", NginxInspect));
        await using var _client = client;

        var image = (await client.InspectImageAsync("nginx:1.29", CancellationToken.None)).Details;

        Assert.Equal(("GET", "/v1.56/images/nginx%3A1.29/json"), stub.Requests[^1]);
        Assert.Equal("sha256:0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9", image.Id);
        Assert.Equal(["nginx:1.29"], image.RepoTags);
        Assert.Equal(["nginx@sha256:9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170615e4d3c2b1a0"], image.RepoDigests);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 18, 0, 28, TimeSpan.Zero).AddTicks(5837141), image.Created);
        Assert.Equal(192000000, image.Size);
        Assert.Equal("amd64", image.Architecture);
        Assert.Equal("linux", image.Os);
        Assert.Equal(["80/tcp", "443/tcp"], image.ExposedPorts);
        Assert.Equal(["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "NGINX_VERSION=1.29.0"], image.Env);
        Assert.Equal(["nginx", "-g", "daemon off;"], image.Cmd);
        Assert.Equal(["/docker-entrypoint.sh"], image.Entrypoint);
        Assert.Equal("/", image.WorkingDir);
        Assert.Equal("someone@example.com", image.Labels["maintainer"]);
    }

    [Fact]
    public async Task Inspect_keeps_the_engines_whole_answer_indented()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/images/nginx%3A1.29/json", NginxInspect));
        await using var _client = client;

        var raw = (await client.InspectImageAsync("nginx:1.29", CancellationToken.None)).RawJson;

        Assert.StartsWith("{\n  \"Id\": \"sha256:0a1b2c3d", raw);
        Assert.Contains("\n    \"StopSignal\": \"SIGQUIT\"", raw);
        Assert.Contains("\"Metadata\": {\n    \"LastTagTime\": \"2026-11-15T16:54:18.375585919Z\"\n  }", raw);
    }

    [Fact]
    public async Task Inspect_escapes_a_name_with_a_registry_and_a_path()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/images/host%3A5000%2Fa%2Fb%3Atag/json", NginxInspect));
        await using var _client = client;

        await client.InspectImageAsync("host:5000/a/b:tag", CancellationToken.None);

        Assert.Equal(("GET", "/v1.56/images/host%3A5000%2Fa%2Fb%3Atag/json"), stub.Requests[^1]);
    }

    [Fact]
    public async Task Inspect_tolerates_a_missing_config_and_null_fields()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/images/abc/json",
            """{"Id":"sha256:abc","RepoTags":null,"RepoDigests":null,"Created":null,"Config":null,"Architecture":null,"Os":null}"""));
        await using var _client = client;

        var image = (await client.InspectImageAsync("abc", CancellationToken.None)).Details;

        Assert.Equal("sha256:abc", image.Id);
        Assert.Null(image.Created);
        Assert.Equal(0, image.Size);
        Assert.Equal("", image.Architecture);
        Assert.Equal("", image.Os);
        Assert.Equal("", image.WorkingDir);
        Assert.Empty(image.RepoTags);
        Assert.Empty(image.RepoDigests);
        Assert.Empty(image.ExposedPorts);
        Assert.Empty(image.Env);
        Assert.Empty(image.Cmd);
        Assert.Empty(image.Entrypoint);
        Assert.Empty(image.Labels);
    }

    [Fact]
    public async Task Inspect_of_a_missing_image_carries_the_engines_404()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/images/berth-check-nope/json", HttpStatusCode.NotFound, """{"message":"No such image: berth-check-nope:latest"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.InspectImageAsync("berth-check-nope", CancellationToken.None));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("No such image: berth-check-nope:latest", error.Message);
    }

    [Fact]
    public async Task Remove_lists_what_was_untagged_and_deleted_and_sends_force_when_asked()
    {
        const string answer = $$"""[{"Untagged":"alpine:3.19"},{"Untagged":"alpine@{{AlpineId}}"},{"Deleted":"{{AlpineId}}"}]""";
        var (client, stub) = await ConnectedAsync(stub => stub
            .On("/v1.56/images/alpine%3A3.19", answer)
            .On("/v1.56/images/alpine%3A3.19?force=true", "[]"));
        await using var _client = client;

        var removed = await client.RemoveImageAsync("alpine:3.19", force: false, CancellationToken.None);
        Assert.Equal(("DELETE", "/v1.56/images/alpine%3A3.19"), stub.Requests[^1]);
        Assert.Equal(
            [new ImageDeleteResult("alpine:3.19", null), new ImageDeleteResult($"alpine@{AlpineId}", null), new ImageDeleteResult(null, AlpineId)],
            removed);

        Assert.Empty(await client.RemoveImageAsync("alpine:3.19", force: true, CancellationToken.None));
        Assert.Equal(("DELETE", "/v1.56/images/alpine%3A3.19?force=true"), stub.Requests[^1]);
    }

    [Fact]
    public async Task Remove_of_an_image_in_use_carries_the_engines_409()
    {
        const string message = "conflict: unable to remove repository reference \"alpine:3.19\" (must force) - container 1f0c2a9e8b7d is using its referenced image 5b1f0e6c2d9a";
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/images/alpine%3A3.19", HttpStatusCode.Conflict, $$"""{"message":"{{message.Replace("\"", "\\\"", StringComparison.Ordinal)}}"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.RemoveImageAsync("alpine:3.19", force: false, CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal(message, error.Message);
    }

    // filters={"dangling":["true"]} and {"dangling":["false"]}, escaped.
    private const string PruneDangling = "/v1.56/images/prune?filters=%7B%22dangling%22%3A%5B%22true%22%5D%7D";
    private const string PruneUnused = "/v1.56/images/prune?filters=%7B%22dangling%22%3A%5B%22false%22%5D%7D";

    [Fact]
    public async Task Prune_sends_the_dangling_filter_and_lists_the_deleted_images()
    {
        var (client, stub) = await ConnectedAsync(stub => stub
            .On(PruneDangling, $$"""{"ImagesDeleted":[{"Untagged":"berth-check/alpine:test"},{"Deleted":"{{DanglingId}}"},{"Deleted":"sha256:2"}],"SpaceReclaimed":123456}""")
            .On(PruneUnused, """{"ImagesDeleted":null,"SpaceReclaimed":0}"""));
        await using var _client = client;

        var dangling = await client.PruneImagesAsync(danglingOnly: true, CancellationToken.None);
        Assert.Equal(("POST", PruneDangling), stub.Requests[^1]);
        Assert.Equal([DanglingId, "sha256:2"], dangling.Deleted);
        Assert.Equal(123456, dangling.SpaceReclaimed);

        var unused = await client.PruneImagesAsync(danglingOnly: false, CancellationToken.None);
        Assert.Equal(("POST", PruneUnused), stub.Requests[^1]);
        Assert.Empty(unused.Deleted);
        Assert.Equal(0, unused.SpaceReclaimed);
    }

    [Fact]
    public async Task Prune_while_another_runs_carries_the_engines_409()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            PruneDangling, HttpStatusCode.Conflict, """{"message":"a prune operation is already running"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.PruneImagesAsync(danglingOnly: true, CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("a prune operation is already running", error.Message);
    }
}

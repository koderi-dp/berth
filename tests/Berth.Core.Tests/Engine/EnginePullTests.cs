using System.Net;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class EnginePullTests
{
    private const string Path = "/v1.56/images/create?fromImage=alpine&tag=3.19";

    private static async Task<(EngineClient Client, StubHandler Stub)> ConnectedAsync(Action<StubHandler> setup)
    {
        var stub = new StubHandler().WithVersion("1.56");
        setup(stub);
        var client = new EngineClient(stub);
        await client.ConnectAsync(CancellationToken.None);
        return (client, stub);
    }

    [Fact]
    public async Task A_pull_reports_each_layer_and_the_final_status_lines()
    {
        var body = new ChunkStream()
            .Write("""{"status":"Pulling from library/alpine","id":"3.19"}""" + "\n")
            .Write("""{"status":"Pulling fs layer","progressDetail":{},"id":"a1b2c3d4e5f6"}""" + "\n")
            .Write("""{"status":"Downloading","progressDetail":{"current":123,"total":456},"progress":"[==>   ]","id":"a1b2c3d4e5f6"}""" + "\n")
            .Write("""{"status":"Extracting","progressDetail":{"current":456,"total":456},"id":"a1b2c3d4e5f6"}""" + "\n")
            .Write("""{"status":"Pull complete","progressDetail":{},"id":"a1b2c3d4e5f6"}""" + "\n")
            .Write("""{"status":"Digest: sha256:0123abcd"}""" + "\n")
            .Write("""{"status":"Status: Downloaded newer image for alpine:3.19"}""" + "\n")
            .End();
        var (client, stub) = await ConnectedAsync(stub => stub.On(Path, body));
        await using var _client = client;

        var lines = await ReadAllAsync(client.PullImageAsync("alpine:3.19", CancellationToken.None));

        Assert.Equal(("POST", Path), stub.Requests[^1]);
        Assert.Equal(
            [
                new PullProgress("3.19", "Pulling from library/alpine", null, null),
                new PullProgress("a1b2c3d4e5f6", "Pulling fs layer", null, null),
                new PullProgress("a1b2c3d4e5f6", "Downloading", 123, 456),
                new PullProgress("a1b2c3d4e5f6", "Extracting", 456, 456),
                new PullProgress("a1b2c3d4e5f6", "Pull complete", null, null),
                new PullProgress("", "Digest: sha256:0123abcd", null, null),
                new PullProgress("", "Status: Downloaded newer image for alpine:3.19", null, null),
            ],
            lines);
    }

    [Fact]
    public async Task A_pull_of_an_image_that_is_up_to_date_reports_what_the_engine_sends()
    {
        // Recorded from a 29.9.3 engine: docker pull alpine with alpine:latest already present.
        var body = new ChunkStream()
            .Write("{\"status\":\"Pulling from library/alpine\",\"id\":\"latest\"}\r\n")
            .Write("{\"status\":\"Digest: sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf\"}\r\n")
            .Write("{\"status\":\"Status: Image is up to date for alpine:latest\"}\r\n")
            .End();
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/images/create?fromImage=alpine&tag=latest", body));
        await using var _client = client;

        var lines = await ReadAllAsync(client.PullImageAsync("alpine", CancellationToken.None));

        Assert.Equal(("POST", "/v1.56/images/create?fromImage=alpine&tag=latest"), stub.Requests[^1]);
        Assert.Equal(3, lines.Count);
        Assert.Equal(new PullProgress("", "Status: Image is up to date for alpine:latest", null, null), lines[^1]);
    }

    [Fact]
    public async Task A_reference_with_a_registry_and_a_digest_is_split_and_escaped()
    {
        const string path = "/v1.56/images/create?fromImage=host%3A5000%2Fa%2Fb&tag=sha256%3A0123abcd";
        var (client, stub) = await ConnectedAsync(stub => stub.On(path, new ChunkStream().End()));
        await using var _client = client;

        Assert.Empty(await ReadAllAsync(client.PullImageAsync("host:5000/a/b@sha256:0123abcd", CancellationToken.None)));

        Assert.Equal(("POST", path), stub.Requests[^1]);
    }

    [Fact]
    public async Task A_blank_reference_is_refused_before_anything_is_sent()
    {
        var (client, stub) = await ConnectedAsync(_ => { });
        await using var _client = client;
        var before = stub.Requests.Count;

        await Assert.ThrowsAsync<ArgumentException>(() => ReadAllAsync(client.PullImageAsync("  ", CancellationToken.None)));

        Assert.Equal(before, stub.Requests.Count);
    }

    [Fact]
    public async Task An_error_line_mid_stream_throws_the_engines_message_after_the_lines_before_it()
    {
        var body = new ChunkStream()
            .Write("""{"status":"Pulling from library/alpine","id":"3.19"}""" + "\n")
            .Write("""{"status":"Downloading","progressDetail":{"current":1,"total":9},"id":"a1b2c3d4e5f6"}""" + "\n")
            .Write("""{"errorDetail":{"message":"read tcp 10.0.0.2:5000: connection reset by peer"},"error":"read tcp: reset"}""" + "\n")
            .Write("""{"status":"never read"}""" + "\n")
            .End();
        var (client, _) = await ConnectedAsync(stub => stub.On(Path, body));
        await using var _client = client;

        var seen = new List<PullProgress>();
        var error = await Assert.ThrowsAsync<EngineException>(async () =>
        {
            await foreach (var line in client.PullImageAsync("alpine:3.19", CancellationToken.None))
            {
                seen.Add(line);
            }
        });

        Assert.Equal(2, seen.Count);
        Assert.Equal(200, error.StatusCode);
        Assert.Equal("read tcp 10.0.0.2:5000: connection reset by peer", error.Message);
    }

    [Fact]
    public async Task An_error_line_without_details_throws_its_error_text()
    {
        var body = new ChunkStream().Write("""{"error":"manifest unknown"}""" + "\n").End();
        var (client, _) = await ConnectedAsync(stub => stub.On(Path, body));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => ReadAllAsync(client.PullImageAsync("alpine:3.19", CancellationToken.None)));

        Assert.Equal("manifest unknown", error.Message);
    }

    [Fact]
    public async Task A_malformed_line_is_skipped()
    {
        var body = new ChunkStream()
            .Write("not json\n")
            .Write("""{"status":"Downloading","progressDetail":{"current":"lots"},"id":"x"}""" + "\n")
            .Write("""{"status":"Pull complete","id":"a1b2c3d4e5f6"}""" + "\n")
            .End();
        var (client, _) = await ConnectedAsync(stub => stub.On(Path, body));
        await using var _client = client;

        var lines = await ReadAllAsync(client.PullImageAsync("alpine:3.19", CancellationToken.None));

        Assert.Equal([new PullProgress("a1b2c3d4e5f6", "Pull complete", null, null)], lines);
    }

    [Fact]
    public async Task A_pull_has_no_deadline_and_hands_over_each_line_as_it_arrives()
    {
        // The body stays open after the first line: a buffered read would never hand that line over.
        var body = new ChunkStream().Write("""{"status":"Pulling from library/alpine","id":"3.19"}""" + "\n");
        var (client, _) = await ConnectedAsync(stub => stub.On(Path, body));
        await using var _client = client;

        await using var lines = client.PullImageAsync("alpine:3.19", CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await lines.MoveNextAsync());

        var next = lines.MoveNextAsync().AsTask();
        Assert.False(next.IsCompleted);
        body.End();

        Assert.False(await next);
    }

    [Fact]
    public async Task Cancellation_stops_the_pull()
    {
        var body = new ChunkStream().Write("""{"status":"Pulling from library/alpine","id":"3.19"}""" + "\n");
        var (client, _) = await ConnectedAsync(stub => stub.On(Path, body));
        await using var _client = client;
        using var cancel = new CancellationTokenSource();

        await using var lines = client.PullImageAsync("alpine:3.19", cancel.Token).GetAsyncEnumerator();
        Assert.True(await lines.MoveNextAsync());

        var next = lines.MoveNextAsync().AsTask();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    [Fact]
    public async Task A_dropped_connection_throws_EngineException_with_status_0()
    {
        var body = new ChunkStream().Write("""{"status":"Pulling from library/alpine","id":"3.19"}""" + "\n");
        var (client, _) = await ConnectedAsync(stub => stub.On(Path, body));
        await using var _client = client;

        await using var lines = client.PullImageAsync("alpine:3.19", CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await lines.MoveNextAsync());
        body.Fail(new IOException("The connection was reset."));

        var error = await Assert.ThrowsAsync<EngineException>(async () => await lines.MoveNextAsync());

        Assert.Equal(0, error.StatusCode);
        Assert.Equal("The connection was reset.", error.Message);
    }

    [Fact]
    public async Task A_refused_pull_carries_the_engines_404()
    {
        // What the engine answers, before any line, for a repository it cannot find.
        const string message = "pull access denied for berth-check-nope, repository does not exist or may require 'docker login'";
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/images/create?fromImage=berth-check-nope&tag=latest", HttpStatusCode.NotFound, $$"""{"message":"{{message}}"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => ReadAllAsync(client.PullImageAsync("berth-check-nope", CancellationToken.None)));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal(message, error.Message);
    }

    private static async Task<List<PullProgress>> ReadAllAsync(IAsyncEnumerable<PullProgress> lines)
    {
        var all = new List<PullProgress>();
        await foreach (var line in lines)
        {
            all.Add(line);
        }

        return all;
    }
}

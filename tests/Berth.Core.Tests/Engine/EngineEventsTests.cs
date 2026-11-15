using System.Net;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class EngineEventsTests
{
    private const string StartWeb = """{"Type":"container","Action":"start","Actor":{"ID":"abc","Attributes":{"name":"web"}},"time":1,"timeNano":1}""";

    private static async Task<(EngineClient Client, StubHandler Stub)> ConnectedAsync(Action<StubHandler> setup)
    {
        var stub = new StubHandler().WithVersion("1.56");
        setup(stub);
        var client = new EngineClient(stub);
        await client.ConnectAsync(CancellationToken.None);
        return (client, stub);
    }

    [Fact]
    public async Task Events_are_parsed()
    {
        var body = new ChunkStream().Write(StartWeb + "\n").End();
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/events", body));
        await using var _client = client;

        var events = await ReadAllAsync(client.StreamEventsAsync(CancellationToken.None));

        var only = Assert.Single(events);
        Assert.Equal("container", only.Type);
        Assert.Equal("start", only.Action);
        Assert.Equal("abc", only.ActorId);
        Assert.Equal(new Dictionary<string, string> { ["name"] = "web" }, only.Attributes);
        Assert.Equal(1, only.TimeNano);
    }

    [Fact]
    public async Task A_missing_actor_or_attributes_gives_an_empty_id_and_no_attributes()
    {
        var body = new ChunkStream()
            .Write("""{"Type":"network","Action":"prune","timeNano":5}""" + "\n")
            .Write("""{"Type":"container","Action":"die","Actor":{"ID":"abc"},"timeNano":6}""" + "\n")
            .End();
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/events", body));
        await using var _client = client;

        var events = await ReadAllAsync(client.StreamEventsAsync(CancellationToken.None));

        Assert.Equal(2, events.Count);
        Assert.Equal("", events[0].ActorId);
        Assert.Empty(events[0].Attributes);
        Assert.Equal(5, events[0].TimeNano);
        Assert.Equal("abc", events[1].ActorId);
        Assert.Empty(events[1].Attributes);
    }

    [Fact]
    public async Task A_malformed_line_is_skipped_and_the_stream_continues()
    {
        var body = new ChunkStream()
            .Write("{\"Type\":\"container\",\"Act\n")
            .Write("not json at all\n")
            .Write(StartWeb + "\n")
            .End();
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/events", body));
        await using var _client = client;

        var events = await ReadAllAsync(client.StreamEventsAsync(CancellationToken.None));

        Assert.Equal("start", Assert.Single(events).Action);
    }

    [Fact]
    public async Task The_request_is_GET_events_and_has_no_timeout()
    {
        // The body stays open after the first event: a buffered read or a deadline would never hand that event over.
        var body = new ChunkStream().Write(StartWeb + "\n");
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/events", body));
        await using var _client = client;

        await using var events = client.StreamEventsAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());

        Assert.Equal("start", events.Current.Action);
        Assert.Equal(("GET", "/v1.56/events"), stub.Requests[^1]);
        body.End();
    }

    [Fact]
    public async Task Enumeration_ends_when_the_engine_closes_the_stream()
    {
        var body = new ChunkStream().Write(StartWeb + "\n");
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/events", body));
        await using var _client = client;

        await using var events = client.StreamEventsAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());

        var next = events.MoveNextAsync().AsTask();
        Assert.False(next.IsCompleted);
        body.End();

        Assert.False(await next);
    }

    [Fact]
    public async Task Cancellation_stops_enumeration()
    {
        var body = new ChunkStream().Write(StartWeb + "\n");
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/events", body));
        await using var _client = client;
        using var cancel = new CancellationTokenSource();

        await using var events = client.StreamEventsAsync(cancel.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());

        var next = events.MoveNextAsync().AsTask();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    [Fact]
    public async Task A_dropped_connection_throws_EngineException_with_status_0()
    {
        var body = new ChunkStream().Write(StartWeb + "\n");
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/events", body));
        await using var _client = client;

        await using var events = client.StreamEventsAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        body.Fail(new IOException("The connection was reset."));

        var error = await Assert.ThrowsAsync<EngineException>(async () => await events.MoveNextAsync());

        Assert.Equal(0, error.StatusCode);
        Assert.Equal("The connection was reset.", error.Message);
    }

    [Fact]
    public async Task An_error_status_carries_the_engines_own_message()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On(
            "/v1.56/events",
            HttpStatusCode.InternalServerError,
            """{"message":"events are unavailable"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(
            () => ReadAllAsync(client.StreamEventsAsync(CancellationToken.None)));

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("events are unavailable", error.Message);
    }

    private static async Task<List<EngineEventMessage>> ReadAllAsync(IAsyncEnumerable<EngineEventMessage> events)
    {
        var all = new List<EngineEventMessage>();
        await foreach (var message in events)
        {
            all.Add(message);
        }

        return all;
    }
}

using System.Net;
using System.Net.Sockets;
using Berth.Engine;
using Berth.Engine.Models;
using Berth.Transport;

namespace Berth.Tests.Engine;

/// <summary>
/// Exec create, resize and inspect against a stub engine, and how exec start reads the engine's answer when it does not switch the
/// connection (against a loopback engine: start writes its request on a connection of its own).
/// </summary>
public sealed class EngineExecTests : IDisposable
{
    private static readonly TerminalSize Size = new(80, 24);

    // A safety net so a broken connection fails the test instead of hanging the run; no test waits on it.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    private static async Task<(EngineClient Client, StubHandler Stub)> ConnectedAsync(Action<StubHandler> setup)
    {
        var stub = new StubHandler().WithVersion("1.56");
        setup(stub);
        var client = new EngineClient(stub);
        await client.ConnectAsync(CancellationToken.None);
        return (client, stub);
    }

    [Fact]
    public async Task Create_posts_the_command_tty_and_console_size()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/containers/abc/exec", HttpStatusCode.Created, """{"Id":"e1"}"""));
        await using var _client = client;

        await client.CreateExecAsync("abc", ["sh"], Size, CancellationToken.None);

        Assert.Equal(("POST", "/v1.56/containers/abc/exec"), stub.Requests[^1]);
        var body = stub.Bodies[^1];
        Assert.Contains("\"Tty\":true", body);
        Assert.Contains("\"AttachStdin\":true", body);
        Assert.Contains("\"AttachStdout\":true", body);
        Assert.Contains("\"AttachStderr\":true", body);
        Assert.Contains("\"Cmd\":[\"sh\"]", body);
        Assert.Contains("\"ConsoleSize\":[24,80]", body);
        Assert.DoesNotContain("\"Env\"", body);
        Assert.DoesNotContain("\"User\"", body);
        Assert.DoesNotContain("\"WorkingDir\"", body);
    }

    [Fact]
    public async Task Create_returns_the_exec_id()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/containers/abc/exec", HttpStatusCode.Created, """{"Id":"e1"}"""));
        await using var _client = client;

        Assert.Equal("e1", await client.CreateExecAsync("abc", ["bash", "-l"], Size, CancellationToken.None));
    }

    [Fact]
    public async Task Create_in_a_paused_container_throws_the_engines_409()
    {
        const string Message = "Container abc is paused, unpause the container before exec";
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/containers/abc/exec", HttpStatusCode.Conflict, $$"""{"message":"{{Message}}"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.CreateExecAsync("abc", ["sh"], Size, CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal(Message, error.Message);
    }

    [Fact]
    public async Task Create_for_a_missing_container_throws_the_engines_404()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/containers/abc/exec", HttpStatusCode.NotFound, """{"message":"No such container: abc"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.CreateExecAsync("abc", ["sh"], Size, CancellationToken.None));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("No such container: abc", error.Message);
    }

    [Fact]
    public async Task Create_with_a_size_berth_never_sends_throws_before_calling_the_engine()
    {
        var (client, stub) = await ConnectedAsync(_ => { });
        await using var _client = client;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CreateExecAsync("abc", ["sh"], new TerminalSize(0, 0), CancellationToken.None));

        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Resize_sends_rows_as_h_and_columns_as_w()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On("/v1.56/exec/e1/resize?h=30&w=120", HttpStatusCode.OK, ""));
        await using var _client = client;

        await client.ResizeExecAsync("e1", new TerminalSize(120, 30), CancellationToken.None);

        Assert.Equal(("POST", "/v1.56/exec/e1/resize?h=30&w=120"), stub.Requests[^1]);
    }

    [Fact]
    public async Task Inspect_reads_running_and_exit_code()
    {
        var (client, stub) = await ConnectedAsync(stub => stub.On(
            "/v1.56/exec/e1/json", """{"CanRemove":false,"ID":"e1","Running":false,"ExitCode":3,"Pid":4242,"ContainerID":"abc"}"""));
        await using var _client = client;

        var inspection = await client.InspectExecAsync("e1", CancellationToken.None);

        Assert.Equal(("GET", "/v1.56/exec/e1/json"), stub.Requests[^1]);
        Assert.Equal(new ExecInspection(Running: false, ExitCode: 3), inspection);
    }

    [Fact]
    public async Task Inspect_of_a_running_exec_has_no_exit_code()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/exec/e1/json", """{"ID":"e1","Running":true,"ExitCode":0}"""));
        await using var _client = client;

        Assert.Equal(new ExecInspection(Running: true, ExitCode: null), await client.InspectExecAsync("e1", CancellationToken.None));
    }

    [Fact]
    public async Task Inspect_tolerates_missing_fields()
    {
        var (client, _) = await ConnectedAsync(stub => stub.On("/v1.56/exec/e1/json", """{"ID":"e1","ExitCode":null}"""));
        await using var _client = client;

        Assert.Equal(new ExecInspection(Running: false, ExitCode: null), await client.InspectExecAsync("e1", CancellationToken.None));
    }

    [Fact]
    public async Task Start_on_a_200_answer_is_refused_with_berths_message()
    {
        await using var engine = await LoopbackAsync(LoopbackEngine.RawStreamOk);

        var error = await Assert.ThrowsAsync<EngineException>(() => engine.Client.StartExecAsync("e1", Size, _timeout.Token));

        Assert.Equal(200, error.StatusCode);
        Assert.Equal("The engine did not switch the connection to a two-way stream.", error.Message);
    }

    [Fact]
    public async Task Start_on_a_409_throws_the_engines_message()
    {
        await using var engine = await LoopbackAsync(LoopbackEngine.JsonError(409, "Conflict", "container abc is not running"));

        var error = await Assert.ThrowsAsync<EngineException>(() => engine.Client.StartExecAsync("e1", Size, _timeout.Token));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("container abc is not running", error.Message);
    }

    [Fact]
    public async Task Start_reads_a_chunked_error_body()
    {
        const string Body = """{"message":"No such exec instance: e1"}""";
        var answer = $"HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n" +
            $"{10:x}\r\n{Body[..10]}\r\n{Body.Length - 10:x};ext=1\r\n{Body[10..]}\r\n0\r\n\r\n";
        await using var engine = await LoopbackAsync(answer);

        var error = await Assert.ThrowsAsync<EngineException>(() => engine.Client.StartExecAsync("e1", Size, _timeout.Token));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("No such exec instance: e1", error.Message);
    }

    [Fact]
    public async Task Start_reads_at_most_64_KB_of_a_chunked_error_body_even_from_one_huge_chunk()
    {
        // The chunk says it is almost 2 GB; 70 KB of it arrive and the connection stays open. Only 64 KB are read.
        var answer = $"HTTP/1.1 409 Conflict\r\nTransfer-Encoding: chunked\r\n\r\n{0x7ffffff0:x}\r\n" + new string('x', 70 * 1024);
        await using var engine = await LoopbackAsync(answer, deadline: TimeSpan.FromSeconds(10));

        var error = await Assert.ThrowsAsync<EngineException>(() => engine.Client.StartExecAsync("e1", Size, _timeout.Token));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("409 Conflict", error.Message);
    }

    [Fact]
    public async Task Start_with_an_error_body_that_is_not_json_throws_the_status_line()
    {
        await using var engine = await LoopbackAsync("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 4\r\n\r\noops");

        var error = await Assert.ThrowsAsync<EngineException>(() => engine.Client.StartExecAsync("e1", Size, _timeout.Token));

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("500 Internal Server Error", error.Message);
    }

    [Fact]
    public async Task Start_on_an_answer_that_is_not_http_throws_status_0()
    {
        await using var engine = await LoopbackAsync("SSH-2.0-OpenSSH_9.6\r\n\r\n");

        var error = await Assert.ThrowsAsync<EngineException>(() => engine.Client.StartExecAsync("e1", Size, _timeout.Token));

        Assert.Equal(0, error.StatusCode);
        Assert.Equal("The engine's answer could not be read.", error.Message);
    }

    [Fact]
    public async Task Start_without_an_answer_in_time_throws_a_timeout()
    {
        await using var engine = await LoopbackAsync("", deadline: TimeSpan.FromMilliseconds(200));

        var error = await Assert.ThrowsAsync<EngineException>(() => engine.Client.StartExecAsync("e1", Size, _timeout.Token));

        Assert.Equal(0, error.StatusCode);
        Assert.True(error.TimedOut);
    }

    [Fact]
    public async Task Start_cancelled_while_waiting_for_the_answer_throws_OperationCanceledException()
    {
        await using var engine = await LoopbackAsync("");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token);

        var start = engine.Client.StartExecAsync("e1", Size, cancel.Token);
        await engine.Server.Request.WaitAsync(_timeout.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
    }

    [Fact]
    public async Task Start_on_an_engine_that_cannot_be_reached_throws_status_0()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        await using var transport = new TcpTransport("127.0.0.1", port, tls: null);
        await using var client = new EngineClient(new StubHandler().WithVersion("1.56"), transport);
        await client.ConnectAsync(_timeout.Token);

        var error = await Assert.ThrowsAsync<EngineException>(() => client.StartExecAsync("e1", Size, _timeout.Token));

        Assert.Equal(0, error.StatusCode);
        Assert.False(error.TimedOut);
    }

    [Fact]
    public async Task Start_on_a_client_without_a_transport_for_streams_throws()
    {
        var (client, _) = await ConnectedAsync(_ => { });
        await using var _client = client;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartExecAsync("e1", Size, CancellationToken.None));
    }

    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(1000, 500, true)]
    [InlineData(80, 24, true)]
    [InlineData(1, 24, false)]
    [InlineData(1001, 24, false)]
    [InlineData(80, 0, false)]
    [InlineData(80, 501, false)]
    public void A_terminal_size_is_valid_from_2_by_1_to_1000_by_500(int columns, int rows, bool valid) =>
        Assert.Equal(valid, new TerminalSize(columns, rows).IsValid);

    /// <summary>A loopback TCP engine that answers exec start with <paramref name="answer"/>, and a client connected to it.</summary>
    private async Task<Loopback> LoopbackAsync(string answer, TimeSpan? deadline = null)
    {
        var server = LoopbackEngine.Start(LoopbackKind.Tcp);
        server.Reply = answer;
        var transport = TransportFactory.Create(server.Profile);
        var client = new EngineClient(EngineHttp.CreateHandler(transport), transport) { PlainTimeout = deadline ?? TimeSpan.FromSeconds(30) };
        var loopback = new Loopback(server, transport, client);
        await client.ConnectAsync(_timeout.Token);
        return loopback;
    }

    private sealed record Loopback(LoopbackEngine Server, IEngineTransport Transport, EngineClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Transport.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}

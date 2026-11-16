using System.Buffers.Binary;
using System.Net;
using System.Text;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

/// <summary>The archive calls (stat, read, write) and the non-TTY exec that returns its output, against a stub engine.</summary>
public sealed class EngineArchiveTests
{
    private const string EtcStat =
        """{"name":"etc","size":4096,"mode":2147484141,"mtime":"2026-11-15T22:29:56.431394779+01:00","linkTarget":""}""";

    private const string LinkStat = """{"name":"sh","size":12,"mode":134218239,"mtime":"2026-11-05T10:19:02+01:00","linkTarget":"/bin/busybox"}""";

    private static string Header(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    private static async Task<(EngineClient Client, StubHandler Stub)> ConnectedAsync(Action<StubHandler> setup)
    {
        var stub = new StubHandler().WithVersion("1.56");
        setup(stub);
        var client = new EngineClient(stub);
        await client.ConnectAsync(CancellationToken.None);
        return (client, stub);
    }

    [Fact]
    public async Task Stat_asks_with_head_and_reads_the_path_stat_header()
    {
        var (client, stub) = await ConnectedAsync(s => s.On(
            "/v1.56/containers/abc/archive?path=%2Fetc", HttpStatusCode.OK, [], ("X-Docker-Container-Path-Stat", Header(EtcStat))));
        await using var _ = client;

        var stat = await client.StatPathAsync("abc", "/etc", CancellationToken.None);

        Assert.Equal(("HEAD", "/v1.56/containers/abc/archive?path=%2Fetc"), stub.Requests[^1]);
        Assert.Equal("etc", stat.Name);
        Assert.Equal(4096, stat.Size);
        Assert.True(stat.IsDirectory);
        Assert.False(stat.IsLink);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 21, 29, 56, TimeSpan.Zero), stat.Modified!.Value.AddTicks(-(stat.Modified.Value.Ticks % TimeSpan.TicksPerSecond)));
    }

    [Fact]
    public async Task Stat_of_a_link_has_its_target()
    {
        var (client, _) = await ConnectedAsync(s => s.On(
            "/v1.56/containers/abc/archive?path=%2Fbin%2Fsh", HttpStatusCode.OK, [], ("X-Docker-Container-Path-Stat", Header(LinkStat))));
        await using var _client = client;

        var stat = await client.StatPathAsync("abc", "/bin/sh", CancellationToken.None);

        Assert.True(stat.IsLink);
        Assert.False(stat.IsDirectory);
        Assert.Equal("/bin/busybox", stat.LinkTarget);
    }

    [Fact]
    public async Task A_path_is_escaped_whole_in_the_query()
    {
        var (client, stub) = await ConnectedAsync(s => s.On(
            "/v1.56/containers/abc/archive?path=%2Ftmp%2Fa%20b%26c%3Dd%2B%23", HttpStatusCode.OK, [], ("X-Docker-Container-Path-Stat", Header(EtcStat))));
        await using var _ = client;

        await client.StatPathAsync("abc", "/tmp/a b&c=d+#", CancellationToken.None);

        Assert.Equal("/v1.56/containers/abc/archive?path=%2Ftmp%2Fa%20b%26c%3Dd%2B%23", stub.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Reading_an_archive_gives_its_stat_and_its_bytes_as_they_come()
    {
        var tar = new byte[] { 1, 2, 3, 4 };
        var (client, stub) = await ConnectedAsync(s => s.On(
            "/v1.56/containers/abc/archive?path=%2Fetc", HttpStatusCode.OK, tar, ("X-Docker-Container-Path-Stat", Header(EtcStat))));
        await using var _ = client;

        await using var archive = await client.GetArchiveAsync("abc", "/etc", CancellationToken.None);
        var read = new MemoryStream();
        await archive.Content.CopyToAsync(read);

        Assert.Equal(("GET", "/v1.56/containers/abc/archive?path=%2Fetc"), stub.Requests[^1]);
        Assert.True(archive.Stat!.IsDirectory);
        Assert.Equal(tar, read.ToArray());
    }

    [Fact]
    public async Task A_missing_path_fails_with_the_engines_message_as_it_is()
    {
        var (client, _) = await ConnectedAsync(s => s.On(
            "/v1.56/containers/abc/archive?path=%2Fnope", HttpStatusCode.NotFound, """{"message":"Could not find the file /nope in container abc"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.GetArchiveAsync("abc", "/nope", CancellationToken.None));

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("Could not find the file /nope in container abc", error.Message);
    }

    [Fact]
    public async Task Writing_an_archive_puts_the_tar_the_writer_produces_into_the_folder()
    {
        var (client, stub) = await ConnectedAsync(s => s.On(
            "/v1.56/containers/abc/archive?path=%2Ftmp%2Fberth-check&noOverwriteDirNonDir=true", HttpStatusCode.OK, ""));
        await using var _ = client;

        await client.PutArchiveAsync(
            "abc",
            "/tmp/berth-check",
            async (stream, ct) =>
            {
                await stream.WriteAsync(new byte[] { 9, 8, 7 }, ct);
                await stream.WriteAsync(new byte[] { 6 }, ct);
            },
            CancellationToken.None);

        Assert.Equal(("PUT", "/v1.56/containers/abc/archive?path=%2Ftmp%2Fberth-check&noOverwriteDirNonDir=true"), stub.Requests[^1]);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, stub.BodyBytes[^1]);
        Assert.Equal("application/x-tar", stub.ContentTypes[^1]);
        Assert.DoesNotContain("copyUIDGID", stub.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task A_refused_write_fails_with_the_engines_message()
    {
        var (client, _) = await ConnectedAsync(s => s.On(
            "/v1.56/containers/abc/archive?path=%2Fetc%2Fhosts&noOverwriteDirNonDir=true",
            HttpStatusCode.BadRequest,
            """{"message":"extraction point is not a directory"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.PutArchiveAsync(
            "abc", "/etc/hosts", (_, _) => Task.CompletedTask, CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("extraction point is not a directory", error.Message);
    }

    /// <summary>A local file that cannot be read while the tar is written; the writer's own exception type, not an IOException.</summary>
    private sealed class LocalReadFailed() : Exception("A file could not be read while it was being sent.");

    [Fact]
    public async Task A_failure_of_the_writer_itself_is_thrown_as_it_is_not_as_an_engine_failure()
    {
        var (client, _) = await ConnectedAsync(s => s.On(
            "/v1.56/containers/abc/archive?path=%2Ftmp&noOverwriteDirNonDir=true", HttpStatusCode.OK, ""));
        await using var _client = client;

        await Assert.ThrowsAsync<LocalReadFailed>(() => client.PutArchiveAsync(
            "abc", "/tmp", (_, _) => throw new LocalReadFailed(), CancellationToken.None));
    }

    private static byte[] Frame(byte stream, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[8 + payload.Length];
        frame[0] = stream;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    [Fact]
    public async Task An_exec_for_output_runs_without_a_terminal_and_returns_stdout_and_the_exit_code()
    {
        var body = Frame(1, "abc").Concat(Frame(2, "warning")).Concat(Frame(1, "\0def")).ToArray();
        var (client, stub) = await ConnectedAsync(s => s
            .On("/v1.56/containers/abc/exec", HttpStatusCode.Created, """{"Id":"e5"}""")
            .On("/v1.56/exec/e5/start", HttpStatusCode.OK, body)
            .On("/v1.56/exec/e5/json", """{"Running":false,"ExitCode":3}"""));
        await using var _ = client;

        var output = await client.RunExecForOutputAsync("abc", ["sh", "-c", "x", "sh", "/a b"], 1024, CancellationToken.None);

        var create = stub.Bodies[stub.Requests.IndexOf(("POST", "/v1.56/containers/abc/exec"))];
        Assert.Contains("\"Tty\":false", create);
        Assert.Contains("\"AttachStdin\":false", create);
        Assert.Contains("\"AttachStdout\":true", create);
        Assert.Contains("\"AttachStderr\":true", create);
        Assert.Contains("\"Cmd\":[\"sh\",\"-c\",\"x\",\"sh\",\"/a b\"]", create);
        Assert.Contains("\"Tty\":false", stub.Bodies[stub.Requests.IndexOf(("POST", "/v1.56/exec/e5/start"))]);
        Assert.Equal("abc\0def"u8.ToArray(), output.Output);
        Assert.False(output.Truncated);
        Assert.Equal(3, output.ExitCode);
    }

    [Fact]
    public async Task An_exec_for_output_stops_reading_at_the_limit_and_says_so()
    {
        var body = Frame(1, "0123456789").Concat(Frame(1, "abcdef")).ToArray();
        var (client, stub) = await ConnectedAsync(s => s
            .On("/v1.56/containers/abc/exec", HttpStatusCode.Created, """{"Id":"e5"}""")
            .On("/v1.56/exec/e5/start", HttpStatusCode.OK, body)
            .On("/v1.56/exec/e5/json", """{"Running":true}"""));
        await using var _ = client;

        var output = await client.RunExecForOutputAsync("abc", ["ls"], 12, CancellationToken.None);

        Assert.Equal("0123456789ab"u8.ToArray(), output.Output);
        Assert.True(output.Truncated);
        Assert.Null(output.ExitCode);
        Assert.DoesNotContain(("GET", "/v1.56/exec/e5/json"), stub.Requests);
    }

    [Fact]
    public async Task An_exec_the_engine_refuses_to_start_fails_with_its_message()
    {
        var (client, _) = await ConnectedAsync(s => s
            .On("/v1.56/containers/abc/exec", HttpStatusCode.Created, """{"Id":"e5"}""")
            .On("/v1.56/exec/e5/start", HttpStatusCode.Conflict, """{"message":"OCI runtime exec failed: exec: \"sh\": executable file not found"}"""));
        await using var _client = client;

        var error = await Assert.ThrowsAsync<EngineException>(() => client.RunExecForOutputAsync("abc", ["sh"], 12, CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.StartsWith("OCI runtime exec failed", error.Message);
    }
}

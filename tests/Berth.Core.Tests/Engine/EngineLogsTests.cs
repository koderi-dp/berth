using System.Text;
using Berth.Engine;
using Berth.Engine.Models;
using static Berth.Tests.Engine.LogFramesTests;

namespace Berth.Tests.Engine;

public sealed class EngineLogsTests
{
    private const string Path = "/v1.56/containers/abc/logs?stdout=true&stderr=true&timestamps=true&tail=100&follow=true";
    private const string Ts = "2026-11-15T21:36:22.237994543Z";
    private static readonly DateTimeOffset Time = new DateTimeOffset(2026, 11, 15, 21, 36, 22, TimeSpan.Zero).AddTicks(2379945);
    private static readonly LogRequest Follow100 = new(100, Follow: true);

    private static async Task<(EngineClient Client, StubHandler Stub)> ConnectedAsync(string path, ChunkStream body)
    {
        var stub = new StubHandler().WithVersion("1.56").On(path, body);
        var client = new EngineClient(stub);
        await client.ConnectAsync(CancellationToken.None);
        return (client, stub);
    }

    [Fact]
    public async Task Request_has_the_stream_flags_tail_and_follow()
    {
        var (client, stub) = await ConnectedAsync(Path, new ChunkStream().End());
        await using var _client = client;

        await ReadAllAsync(client.StreamLogsAsync("abc", tty: false, Follow100, CancellationToken.None));

        Assert.Equal(("GET", Path), stub.Requests[^1]);
    }

    [Fact]
    public async Task Tail_minus_one_asks_for_all()
    {
        const string all = "/v1.56/containers/abc/logs?stdout=true&stderr=true&timestamps=true&tail=all&follow=false";
        var (client, stub) = await ConnectedAsync(all, new ChunkStream().End());
        await using var _client = client;

        await ReadAllAsync(client.StreamLogsAsync("abc", tty: false, new LogRequest(-1, Follow: false), CancellationToken.None));

        Assert.Equal(("GET", all), stub.Requests[^1]);
    }

    [Fact]
    public async Task A_line_split_across_two_frames_is_one_line()
    {
        var body = new ChunkStream().Write(Frame(1, $"{Ts} hel")).Write(Frame(1, "lo\n")).End();
        var (client, _) = await ConnectedAsync(Path, body);
        await using var _client = client;

        var lines = await ReadAllAsync(client.StreamLogsAsync("abc", tty: false, Follow100, CancellationToken.None));

        Assert.Equal([new LogLine(LogStream.Stdout, Time, "hello")], lines);
    }

    [Fact]
    public async Task Stdout_and_stderr_lines_do_not_mix_inside_a_line()
    {
        var body = new ChunkStream()
            .Write(Frame(1, $"{Ts} out-"))
            .Write(Frame(2, $"{Ts} err\n"))
            .Write(Frame(1, "part\n"))
            .End();
        var (client, _) = await ConnectedAsync(Path, body);
        await using var _client = client;

        var lines = await ReadAllAsync(client.StreamLogsAsync("abc", tty: false, Follow100, CancellationToken.None));

        Assert.Equal([new LogLine(LogStream.Stderr, Time, "err"), new LogLine(LogStream.Stdout, Time, "out-part")], lines);
    }

    [Fact]
    public async Task Timestamps_are_parsed_and_removed_from_the_text()
    {
        var body = new ChunkStream()
            .Write(Frame(1, $"{Ts} \u001b[32mready\u001b[0m on port 80\n"))
            .Write(Frame(2, "2026-11-15T21:36:23Z second line\n"))
            .End();
        var (client, _) = await ConnectedAsync(Path, body);
        await using var _client = client;

        var lines = await ReadAllAsync(client.StreamLogsAsync("abc", tty: false, Follow100, CancellationToken.None));

        Assert.Equal(
            [
                new LogLine(LogStream.Stdout, Time, "ready on port 80"),
                new LogLine(LogStream.Stderr, new DateTimeOffset(2026, 11, 15, 21, 36, 23, TimeSpan.Zero), "second line"),
            ],
            lines);
    }

    [Fact]
    public async Task A_line_without_a_timestamp_keeps_its_text()
    {
        var body = new ChunkStream().Write(Frame(1, "10:30 no timestamp here\n")).Write(Frame(1, "\n")).End();
        var (client, _) = await ConnectedAsync(Path, body);
        await using var _client = client;

        var lines = await ReadAllAsync(client.StreamLogsAsync("abc", tty: false, Follow100, CancellationToken.None));

        Assert.Equal([new LogLine(LogStream.Stdout, null, "10:30 no timestamp here"), new LogLine(LogStream.Stdout, null, "")], lines);
    }

    [Fact]
    public async Task A_tty_stream_is_read_as_plain_lines()
    {
        // No frame headers: the bytes are the text, all of it on stdout, in reads that split lines anywhere.
        var body = new ChunkStream().Write($"{Ts} fir").Write($"st\r\n{Ts} second\r\n{Ts} th").Write("ird").End();
        var (client, _) = await ConnectedAsync(Path, body);
        await using var _client = client;

        var lines = await ReadAllAsync(client.StreamLogsAsync("abc", tty: true, Follow100, CancellationToken.None));

        Assert.Equal(
            [
                new LogLine(LogStream.Stdout, Time, "first"),
                new LogLine(LogStream.Stdout, Time, "second"),
                new LogLine(LogStream.Stdout, Time, "third"),
            ],
            lines);
    }

    [Fact]
    public async Task Carriage_return_line_endings_are_handled()
    {
        var body = new ChunkStream().Write(Frame(1, $"{Ts} one\r\n{Ts} two\r")).Write(Frame(1, "\n")).End();
        var (client, _) = await ConnectedAsync(Path, body);
        await using var _client = client;

        var lines = await ReadAllAsync(client.StreamLogsAsync("abc", tty: false, Follow100, CancellationToken.None));

        Assert.Equal([new LogLine(LogStream.Stdout, Time, "one"), new LogLine(LogStream.Stdout, Time, "two")], lines);
    }

    [Fact]
    public async Task Invalid_utf8_becomes_the_replacement_character()
    {
        byte[] payload = [.. Encoding.UTF8.GetBytes($"{Ts} caf"), 0xFF, .. "\n"u8];
        var body = new ChunkStream().Write(Frame(1, payload)).End();
        var (client, _) = await ConnectedAsync(Path, body);
        await using var _client = client;

        var lines = await ReadAllAsync(client.StreamLogsAsync("abc", tty: false, Follow100, CancellationToken.None));

        Assert.Equal([new LogLine(LogStream.Stdout, Time, "caf�")], lines);
    }

    [Fact]
    public async Task A_line_over_64_kb_is_cut_with_an_ellipsis()
    {
        // The cut is at 65,536 bytes. In the second line "a" then two-byte characters put that point inside a character,
        // so the cut falls before it rather than leaving half a character.
        var ascii = new string('x', 70_000);
        var wide = "a" + new string('é', 40_000);
        var body = new ChunkStream()
            .Write(Frame(1, ascii[..30_000]))
            .Write(Frame(1, ascii[30_000..] + "\n"))
            .Write(Frame(1, wide + "\n"))
            .Write(Frame(1, "next\n"))
            .End();
        var (client, _) = await ConnectedAsync(Path, body);
        await using var _client = client;

        var lines = await ReadAllAsync(client.StreamLogsAsync("abc", tty: false, Follow100, CancellationToken.None));

        Assert.Equal(
            [
                new LogLine(LogStream.Stdout, null, new string('x', 65_536) + "…"),
                new LogLine(LogStream.Stdout, null, "a" + new string('é', 32_767) + "…"),
                new LogLine(LogStream.Stdout, null, "next"),
            ],
            lines);
    }

    [Fact]
    public async Task The_stream_ending_ends_the_enumeration()
    {
        var body = new ChunkStream().Write(Frame(1, $"{Ts} first\n"));
        var (client, _) = await ConnectedAsync(Path, body);
        await using var _client = client;

        await using var lines = client.StreamLogsAsync("abc", tty: false, Follow100, CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await lines.MoveNextAsync());
        Assert.Equal("first", lines.Current.Text);

        var next = lines.MoveNextAsync().AsTask();
        Assert.False(next.IsCompleted);
        body.Write(Frame(2, $"{Ts} unfinished")).End();

        // The last line has no newline: it is handed over when the stream ends, then the enumeration ends.
        Assert.True(await next);
        Assert.Equal(new LogLine(LogStream.Stderr, Time, "unfinished"), lines.Current);
        Assert.False(await lines.MoveNextAsync());
    }

    [Fact]
    public async Task Cancellation_stops_it()
    {
        var body = new ChunkStream().Write(Frame(1, $"{Ts} first\n"));
        var (client, _) = await ConnectedAsync(Path, body);
        await using var _client = client;
        using var cancel = new CancellationTokenSource();

        await using var lines = client.StreamLogsAsync("abc", tty: false, Follow100, cancel.Token).GetAsyncEnumerator();
        Assert.True(await lines.MoveNextAsync());

        var next = lines.MoveNextAsync().AsTask();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    [Fact]
    public async Task A_dropped_connection_is_an_EngineException_with_status_0()
    {
        var body = new ChunkStream().Write(Frame(1, $"{Ts} first\n"));
        var (client, _) = await ConnectedAsync(Path, body);
        await using var _client = client;

        await using var lines = client.StreamLogsAsync("abc", tty: false, Follow100, CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await lines.MoveNextAsync());
        body.Fail(new IOException("The connection was reset."));

        var error = await Assert.ThrowsAsync<EngineException>(async () => await lines.MoveNextAsync());

        Assert.Equal(0, error.StatusCode);
        Assert.Equal("The connection was reset.", error.Message);
    }

    private static async Task<List<LogLine>> ReadAllAsync(IAsyncEnumerable<LogLine> lines)
    {
        var all = new List<LogLine>();
        await foreach (var line in lines)
        {
            all.Add(line);
        }

        return all;
    }
}

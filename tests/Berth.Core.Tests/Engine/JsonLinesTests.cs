using System.Text;
using Berth.Engine;

namespace Berth.Tests.Engine;

public sealed class JsonLinesTests
{
    [Fact]
    public async Task A_line_split_across_chunks_arrives_whole()
    {
        var body = new ChunkStream().Write("{\"a\":").Write("1}\n{\"b\":2}\n").End();

        var lines = await ReadAllAsync(body);

        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], lines);
    }

    [Fact]
    public async Task A_last_line_without_newline_is_yielded_at_end_of_stream()
    {
        var body = new ChunkStream().Write("{\"a\":1}\n{\"b\":").Write("2}").End();

        var lines = await ReadAllAsync(body);

        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], lines);
    }

    [Fact]
    public async Task Blank_lines_are_skipped()
    {
        var body = new ChunkStream().Write("\n{\"a\":1}\n\n  \r\n").Write("\n{\"b\":2}\r\n\n").End();

        var lines = await ReadAllAsync(body);

        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], lines);
    }

    [Fact]
    public async Task A_line_longer_than_the_buffer_is_handled()
    {
        var longLine = "{\"a\":\"" + new string('x', 200 * 1024) + "\"}";
        var body = new ChunkStream();
        foreach (var chunk in Encoding.UTF8.GetBytes(longLine + "\n{\"b\":2}\n").Chunk(1000))
        {
            body.Write(chunk);
        }

        body.End();

        var lines = await ReadAllAsync(body);

        Assert.Equal([longLine, "{\"b\":2}"], lines);
    }

    private static async Task<List<string>> ReadAllAsync(Stream body)
    {
        var lines = new List<string>();
        await foreach (var line in JsonLines.ReadAsync(body, CancellationToken.None))
        {
            lines.Add(Encoding.UTF8.GetString(line.Span));
        }

        return lines;
    }
}

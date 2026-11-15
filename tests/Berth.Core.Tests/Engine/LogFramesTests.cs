using System.Buffers.Binary;
using System.Text;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class LogFramesTests
{
    /// <summary>One frame of the engine's multiplexed stream: stream id, three zero bytes, big-endian length, payload.</summary>
    internal static byte[] Frame(byte streamId, string payload) => Frame(streamId, Encoding.UTF8.GetBytes(payload));

    internal static byte[] Frame(byte streamId, byte[] payload)
    {
        var frame = new byte[8 + payload.Length];
        frame[0] = streamId;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    [Fact]
    public async Task Reads_stdout_and_stderr_frames()
    {
        var body = new ChunkStream().Write([.. Frame(1, "out\n"), .. Frame(2, "err\n")]).End();

        var frames = await ReadAllAsync(body);

        Assert.Equal([(LogStream.Stdout, "out\n"), (LogStream.Stderr, "err\n")], frames);
    }

    [Fact]
    public async Task A_frame_split_across_reads_is_whole()
    {
        var frame = Frame(2, "a payload in three reads");
        var body = new ChunkStream().Write(frame[..3]).Write(frame[3..12]).Write(frame[12..]).End();

        var frames = await ReadAllAsync(body);

        Assert.Equal([(LogStream.Stderr, "a payload in three reads")], frames);
    }

    [Fact]
    public async Task A_truncated_last_frame_ends_the_stream_without_throwing()
    {
        var cut = Frame(1, "this one never finishes");
        var body = new ChunkStream().Write(Frame(1, "whole\n")).Write(cut[..14]).End();

        var frames = await ReadAllAsync(body);

        Assert.Equal([(LogStream.Stdout, "whole\n")], frames);
    }

    [Fact]
    public async Task A_zero_length_frame_is_skipped()
    {
        var body = new ChunkStream().Write([.. Frame(1, "a"), .. Frame(2, ""), .. Frame(1, "b")]).End();

        var frames = await ReadAllAsync(body);

        Assert.Equal([(LogStream.Stdout, "a"), (LogStream.Stdout, "b")], frames);
    }

    [Fact]
    public async Task An_unknown_stream_id_counts_as_stdout()
    {
        var body = new ChunkStream().Write([.. Frame(0, "stdin"), .. Frame(7, "seven"), .. Frame(2, "err")]).End();

        var frames = await ReadAllAsync(body);

        Assert.Equal([(LogStream.Stdout, "stdin"), (LogStream.Stdout, "seven"), (LogStream.Stderr, "err")], frames);
    }

    [Fact]
    public async Task A_frame_longer_than_16_MiB_ends_with_an_EngineException()
    {
        // A TTY log read as framed: "2026-11-" is a header announcing a 758 MB frame. Nothing is buffered waiting for it.
        var body = new ChunkStream().Write("2026-11-15T21:36:22.237994543Z hello\n");

        var error = await Assert.ThrowsAsync<EngineException>(() => ReadAllAsync(body)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("The log stream is not in the expected format.", error.Message);
        body.End();
    }

    [Fact]
    public async Task A_frame_of_exactly_16_MiB_is_read()
    {
        var payload = new byte[16 * 1024 * 1024];
        payload.AsSpan().Fill((byte)'x');
        var body = new ChunkStream().Write(Frame(1, payload)).End();

        var frames = await ReadAllAsync(body);

        Assert.Equal(payload.Length, Assert.Single(frames).Item2.Length);
    }

    private static async Task<List<(LogStream, string)>> ReadAllAsync(Stream body)
    {
        var all = new List<(LogStream, string)>();
        await foreach (var (stream, payload) in LogFrames.ReadAsync(body, CancellationToken.None))
        {
            all.Add((stream, Encoding.UTF8.GetString(payload.Span)));
        }

        return all;
    }
}

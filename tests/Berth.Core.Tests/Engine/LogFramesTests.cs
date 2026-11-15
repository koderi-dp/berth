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

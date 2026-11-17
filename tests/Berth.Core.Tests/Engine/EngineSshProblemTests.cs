using Berth.Connections;
using Berth.Engine;
using Berth.Engine.Models;
using Berth.Transport;

namespace Berth.Tests.Engine;

/// <summary>An SSH transport's failure reaches the session as the problem it is, through the same engine client as any other failure.</summary>
public sealed class EngineSshProblemTests
{
    private static readonly SshProblem Unknown = new(SshFailure.HostKeyUnknown, "") { Offered = new HostKey("host", 22, "ssh-ed25519", "SHA256:abc") };

    [Fact]
    public async Task A_problem_thrown_while_connecting_is_on_the_engine_exception()
    {
        var transport = new FailingTransport(new SshTransportException(Unknown));
        await using var client = new EngineClient(EngineHttp.CreateHandler(transport), transport);

        var ex = await Assert.ThrowsAsync<EngineException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Equal(0, ex.StatusCode);
        Assert.Same(Unknown, ex.Ssh);
        Assert.Equal(Unknown.Message, ex.Message);
    }

    [Fact]
    public async Task A_problem_thrown_while_starting_an_exec_is_on_the_engine_exception()
    {
        var transport = new FailingTransport(new SshTransportException(Unknown));
        await using var client = new EngineClient(new StubHandler().WithVersion("1.56"), transport);
        await client.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<EngineException>(() => client.StartExecAsync("e1", new TerminalSize(80, 24), CancellationToken.None));

        Assert.Same(Unknown, ex.Ssh);
    }

    [Fact]
    public async Task Any_other_failure_has_no_ssh_problem()
    {
        var transport = new FailingTransport(new IOException("refused"));
        await using var client = new EngineClient(EngineHttp.CreateHandler(transport), transport);

        var ex = await Assert.ThrowsAsync<EngineException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Null(ex.Ssh);
    }

    [Fact]
    public async Task The_exec_stream_closes_the_write_side_of_a_transport_stream_that_can()
    {
        var stream = new HalfClosableStream();

        Assert.True(await new UpgradedExecStream(stream, ReadOnlyMemory<byte>.Empty).CloseWriteAsync());
        Assert.True(stream.WriteClosed);
    }

    private sealed class FailingTransport(Exception failure) : IEngineTransport
    {
        public ValueTask<Stream> OpenAsync(CancellationToken ct) => ValueTask.FromException<Stream>(failure);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HalfClosableStream : MemoryStream, IHalfClosable
    {
        public bool WriteClosed { get; private set; }

        public Task CloseWriteAsync()
        {
            WriteClosed = true;
            return Task.CompletedTask;
        }
    }
}

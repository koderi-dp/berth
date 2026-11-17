using Berth.Connections;

namespace Berth.Tests.Connections;

public sealed class EngineAddressTests
{
    [Theory]
    [InlineData("tcp://127.0.0.1:2375", TransportKind.Tcp, "127.0.0.1", 2375, "")]
    [InlineData("tcp://build-host", TransportKind.Tcp, "build-host", 2375, "")]
    [InlineData("TCP://[::1]:2376", TransportKind.Tcp, "::1", 2376, "")]
    [InlineData("npipe:////./pipe/docker_engine", TransportKind.NamedPipe, ".", 0, "docker_engine")]
    [InlineData("npipe://./pipe/custom", TransportKind.NamedPipe, ".", 0, "custom")]
    public void Parses(string text, TransportKind kind, string host, int port, string pipe)
    {
        var ok = EngineAddress.TryParse(text, out var parsedKind, out var parsedHost, out var parsedPort, out var parsedPipe, out var error);

        Assert.True(ok);
        Assert.Equal(kind, parsedKind);
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
        Assert.Equal(pipe, parsedPipe);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("", "Enter an address.")]
    [InlineData("http://x", "Use tcp://, npipe:// or ssh://.")]
    [InlineData("tcp://", "The address has no host.")]
    [InlineData("tcp://host:99999", "The port must be between 1 and 65535.")]
    [InlineData("npipe://./nope/x", "A pipe address looks like npipe:////./pipe/docker_engine.")]
    public void Rejects(string text, string error)
    {
        var ok = EngineAddress.TryParse(text, out _, out _, out _, out _, out var parsedError);

        Assert.False(ok);
        Assert.Equal(error, parsedError);
    }

    [Fact]
    public void Format_round_trips_both_kinds()
    {
        var tcp = new ConnectionProfile("a", "Remote", TransportKind.Tcp, "::1", 2376, "", null);
        var pipe = new ConnectionProfile("b", "Local", TransportKind.NamedPipe, ".", 0, "docker_engine", null);

        var tcpText = EngineAddress.Format(tcp);
        var pipeText = EngineAddress.Format(pipe);

        Assert.Equal("tcp://[::1]:2376", tcpText);
        Assert.Equal("npipe:////./pipe/docker_engine", pipeText);

        Assert.True(EngineAddress.TryParse(tcpText, out var kind, out var host, out var port, out var pipeName, out _));
        Assert.Equal((TransportKind.Tcp, "::1", 2376, ""), (kind, host, port, pipeName));

        Assert.True(EngineAddress.TryParse(pipeText, out kind, out host, out port, out pipeName, out _));
        Assert.Equal((TransportKind.NamedPipe, ".", 0, "docker_engine"), (kind, host, port, pipeName));
    }
}

using Berth.Connections;

namespace Berth.Tests.Connections;

public sealed class FirstRunTests
{
    [Fact]
    public void Docker_host_set_gives_one_connection_from_it()
    {
        var settings = FirstRun.Create("tcp://127.0.0.1:2375");

        var connection = Assert.Single(settings.Connections);
        Assert.Equal("Docker (tcp://127.0.0.1:2375)", connection.Name);
        Assert.Equal(TransportKind.Tcp, connection.Kind);
        Assert.Equal("127.0.0.1", connection.Host);
        Assert.Equal(2375, connection.Port);
        Assert.Null(connection.Tls);
        Assert.Equal(connection.Id, settings.ActiveConnectionId);
        Assert.Matches("^[0-9a-f]{32}$", connection.Id);
    }

    [Fact]
    public void Docker_host_unset_gives_the_default_pipe()
    {
        var settings = FirstRun.Create(null);

        AssertDefaultPipe(settings);
    }

    [Fact]
    public void Docker_host_unparseable_falls_back_to_default_pipe()
    {
        var settings = FirstRun.Create("http://not-an-engine");

        AssertDefaultPipe(settings);
    }

    private static void AssertDefaultPipe(Berth.Settings.AppSettings settings)
    {
        var connection = Assert.Single(settings.Connections);
        Assert.Equal("Local engine", connection.Name);
        Assert.Equal(TransportKind.NamedPipe, connection.Kind);
        Assert.Equal("docker_engine", connection.PipeName);
        Assert.Equal(connection.Id, settings.ActiveConnectionId);
    }
}

using Berth.Connections;
using Berth.Settings;

namespace Berth.Tests.Settings;

public sealed class FirstRunTests
{
    [Fact]
    public void Docker_host_set_gives_one_connection_from_it()
    {
        var settings = FirstRun.Create("tcp://127.0.0.1:2375");

        var connection = Assert.Single(settings.Connections);
        Assert.Equal("Local engine", connection.Name);
        Assert.Equal(TransportKind.Tcp, connection.Kind);
        Assert.Equal("127.0.0.1", connection.Host);
        Assert.Equal(2375, connection.Port);
        Assert.Null(connection.Tls);
        Assert.Equal(connection.Id, settings.ActiveConnectionId);
        Assert.Matches("^[0-9a-f]{32}$", connection.Id);
    }

    [Theory]
    [InlineData("tcp://127.0.0.1:2375")]
    [InlineData("tcp://127.0.0.2:2375")]
    [InlineData("tcp://localhost:2375")]
    [InlineData("tcp://LOCALHOST")]
    [InlineData("tcp://[::1]:2375")]
    [InlineData("npipe:////./pipe/dockerDesktopLinuxEngine")]
    public void Docker_host_on_this_machine_is_named_local_engine(string dockerHost) =>
        Assert.Equal("Local engine", Assert.Single(FirstRun.Create(dockerHost).Connections).Name);

    [Theory]
    [InlineData("tcp://build-host:2375", "build-host")]
    [InlineData("tcp://10.0.0.5:2376", "10.0.0.5")]
    [InlineData("tcp://[fd00::5]:2375", "fd00::5")]
    [InlineData("npipe:////buildbox/pipe/docker_engine", "buildbox")]
    public void Docker_host_elsewhere_is_named_by_its_host(string dockerHost, string name) =>
        Assert.Equal(name, Assert.Single(FirstRun.Create(dockerHost).Connections).Name);

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

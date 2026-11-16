using Berth.Connections;

namespace Berth.Tests.Connections;

public sealed class EngineControlTests
{
    [Fact]
    public void The_wsl_default_starts_docker_and_stops_its_socket_too_as_root()
    {
        var control = EngineControl.WslDefault("Ubuntu-24.04");

        Assert.Equal("wsl.exe -d Ubuntu-24.04 -u root -- systemctl start docker", control.StartCommand);
        Assert.Equal("wsl.exe -d Ubuntu-24.04 -u root -- systemctl stop docker.socket docker", control.StopCommand);
    }

    [Fact]
    public void The_wsl_default_quotes_a_distro_name_that_needs_it()
    {
        var control = EngineControl.WslDefault("My Linux");

        Assert.Equal<string>(["-d", "My Linux", "-u", "root", "--", "systemctl", "start", "docker"], CommandLine.Parse(control.StartCommand).Arguments);
    }
}

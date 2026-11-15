using Berth.Engine.Models;
using Berth.Presentation;
using static Berth.Tests.Presentation.DetailsFixtures;

namespace Berth.Tests.Presentation;

public sealed class DockerRunCommandTests
{
    [Fact]
    public void Container_with_nothing_optional_gives_name_and_image()
    {
        Assert.Equal("docker run -d --name web nginx:alpine", DockerRunCommand.Build(Details()));
    }

    [Fact]
    public void Published_ports_are_given_once_each_and_unpublished_ones_are_left_out()
    {
        var details = Details() with
        {
            Ports =
            [
                new PortMapping("0.0.0.0", 80, 8080, "tcp"),
                new PortMapping("::", 80, 8080, "tcp"),
                new PortMapping(null, 443, null, "tcp"),
                new PortMapping("0.0.0.0", 53, 5353, "udp"),
                new PortMapping("127.0.0.1", 9000, 9001, "tcp"),
            ],
        };

        Assert.Equal(
            "docker run -d --name web -p 5353:53/udp -p 8080:80 -p 127.0.0.1:9001:9000 nginx:alpine",
            DockerRunCommand.Build(details));
    }

    [Fact]
    public void Mounts_give_their_source_and_destination_and_read_only_ones_end_with_ro()
    {
        var details = Details() with
        {
            Mounts =
            [
                new MountDetails("bind", "/srv/data", "/data", "", true, null),
                new MountDetails("volume", "/var/lib/docker/volumes/cache/_data", "/cache", "z", false, "cache"),
            ],
        };

        Assert.Equal(
            "docker run -d --name web -v /srv/data:/data -v cache:/cache:ro nginx:alpine",
            DockerRunCommand.Build(details));
    }

    [Fact]
    public void Tmpfs_mount_is_given_with_tmpfs_because_it_has_no_source()
    {
        var details = Details() with { Mounts = [new MountDetails("tmpfs", "", "/run/cache", "", true, null)] };

        Assert.Equal("docker run -d --name web --tmpfs /run/cache nginx:alpine", DockerRunCommand.Build(details));
    }

    [Fact]
    public void Environment_entries_are_given_with_e()
    {
        var details = Details() with { Env = ["A=1", "PATH=/usr/local/bin:/usr/bin"] };

        Assert.Equal(
            "docker run -d --name web -e A=1 -e PATH=/usr/local/bin:/usr/bin nginx:alpine",
            DockerRunCommand.Build(details));
    }

    [Theory]
    [InlineData("always", "docker run -d --name web --restart always nginx:alpine")]
    [InlineData("unless-stopped", "docker run -d --name web --restart unless-stopped nginx:alpine")]
    [InlineData("on-failure", "docker run -d --name web --restart on-failure nginx:alpine")]
    [InlineData("no", "docker run -d --name web nginx:alpine")]
    [InlineData("", "docker run -d --name web nginx:alpine")]
    public void Restart_policy_is_given_unless_it_is_no(string policy, string expected)
    {
        Assert.Equal(expected, DockerRunCommand.Build(Details() with { RestartPolicy = policy }));
    }

    [Fact]
    public void Working_directory_user_and_tty_are_given_when_set()
    {
        var details = Details() with { WorkingDir = "/app", User = "1000:1000", Tty = true };

        Assert.Equal("docker run -d --name web -w /app -u 1000:1000 -t nginx:alpine", DockerRunCommand.Build(details));
    }

    [Fact]
    public void Command_follows_the_image()
    {
        var details = Details() with { Cmd = ["nginx", "-g", "daemon off;"] };

        Assert.Equal("docker run -d --name web nginx:alpine nginx -g \"daemon off;\"", DockerRunCommand.Build(details));
    }

    [Fact]
    public void Arguments_with_spaces_or_shell_characters_are_quoted_and_inner_quotes_escaped()
    {
        var details = Details() with
        {
            Env = ["GREETING=hello world", "HOME_BIN=$HOME/bin", "EMPTY="],
            Mounts = [new MountDetails("bind", @"C:\My Data", "/data", "", true, null)],
            Cmd = ["sh", "-c", "echo \"hi\" && sleep 1", ""],
        };

        Assert.Equal(
            "docker run -d --name web -v \"C:\\My Data:/data\" -e \"GREETING=hello world\" -e \"HOME_BIN=$HOME/bin\" "
            + "-e EMPTY= nginx:alpine sh -c \"echo \\\"hi\\\" && sleep 1\" \"\"",
            DockerRunCommand.Build(details));
    }

    [Fact]
    public void All_options_come_in_order_before_the_image_and_command()
    {
        var details = Details("api") with
        {
            Ports = [new PortMapping("0.0.0.0", 3000, 3000, "tcp")],
            Mounts = [new MountDetails("bind", "/srv/api", "/app", "", true, null)],
            Env = ["NODE_ENV=production"],
            RestartPolicy = "always",
            WorkingDir = "/app",
            User = "node",
            Tty = true,
            Image = "node:22",
            Cmd = ["node", "server.js"],
        };

        Assert.Equal(
            "docker run -d --name api -p 3000:3000 -v /srv/api:/app -e NODE_ENV=production --restart always "
            + "-w /app -u node -t node:22 node server.js",
            DockerRunCommand.Build(details));
    }
}

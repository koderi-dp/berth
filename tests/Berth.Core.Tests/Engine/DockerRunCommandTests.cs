using Berth.Engine.Models;
using Berth.Engine;
using static Berth.Tests.Presentation.DetailsFixtures;

namespace Berth.Tests.Engine;

public sealed class DockerRunCommandTests
{
    [Theory]
    [InlineData(ShellKind.Posix)]
    [InlineData(ShellKind.PowerShell)]
    public void Container_with_nothing_optional_gives_name_and_image(ShellKind shell)
    {
        Assert.Equal("docker run -d --name web nginx:alpine", DockerRunCommand.Build(Details(), shell));
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
            DockerRunCommand.Build(details, ShellKind.Posix));
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
            DockerRunCommand.Build(details, ShellKind.Posix));
    }

    [Fact]
    public void Tmpfs_mount_is_given_with_tmpfs_because_it_has_no_source()
    {
        var details = Details() with { Mounts = [new MountDetails("tmpfs", "", "/run/cache", "", true, null)] };

        Assert.Equal("docker run -d --name web --tmpfs /run/cache nginx:alpine", DockerRunCommand.Build(details, ShellKind.Posix));
    }

    [Fact]
    public void Environment_entries_are_given_with_e()
    {
        var details = Details() with { Env = ["A=1", "PATH=/usr/local/bin:/usr/bin"] };

        Assert.Equal(
            "docker run -d --name web -e A=1 -e PATH=/usr/local/bin:/usr/bin nginx:alpine",
            DockerRunCommand.Build(details, ShellKind.Posix));
    }

    [Theory]
    [InlineData("always", "docker run -d --name web --restart always nginx:alpine")]
    [InlineData("unless-stopped", "docker run -d --name web --restart unless-stopped nginx:alpine")]
    [InlineData("on-failure", "docker run -d --name web --restart on-failure nginx:alpine")]
    [InlineData("no", "docker run -d --name web nginx:alpine")]
    [InlineData("", "docker run -d --name web nginx:alpine")]
    public void Restart_policy_is_given_unless_it_is_no(string policy, string expected)
    {
        Assert.Equal(expected, DockerRunCommand.Build(Details() with { RestartPolicy = policy }, ShellKind.Posix));
    }

    [Fact]
    public void Working_directory_user_and_tty_are_given_when_set()
    {
        var details = Details() with { WorkingDir = "/app", User = "1000:1000", Tty = true };

        Assert.Equal("docker run -d --name web -w /app -u 1000:1000 -t nginx:alpine", DockerRunCommand.Build(details, ShellKind.Posix));
    }

    [Fact]
    public void Command_follows_the_image()
    {
        var details = Details() with { Cmd = ["nginx", "-g", "daemon off;"] };

        Assert.Equal("docker run -d --name web nginx:alpine nginx -g 'daemon off;'", DockerRunCommand.Build(details, ShellKind.Posix));
    }

    [Theory]
    [InlineData(ShellKind.Posix, "$HOME/bin", "'$HOME/bin'")]
    [InlineData(ShellKind.Posix, "`id`", "'`id`'")]
    [InlineData(ShellKind.Posix, "$(id)", "'$(id)'")]
    [InlineData(ShellKind.Posix, "hi!", "'hi!'")]
    [InlineData(ShellKind.Posix, "say \"hi\"", "'say \"hi\"'")]
    [InlineData(ShellKind.Posix, "it's", @"'it'\''s'")]
    [InlineData(ShellKind.Posix, "it’s", "'it’s'")]
    [InlineData(ShellKind.Posix, "a b", "'a b'")]
    [InlineData(ShellKind.Posix, "a,b", "'a,b'")]
    [InlineData(ShellKind.Posix, "one\ntwo", "'one\ntwo'")]
    [InlineData(ShellKind.Posix, "", "''")]
    [InlineData(ShellKind.Posix, @"C:\data\", @"'C:\data\'")]
    [InlineData(ShellKind.Posix, "@file", "@file")]
    [InlineData(ShellKind.Posix, "$2y$10$abc", "'$2y$10$abc'")]
    [InlineData(ShellKind.PowerShell, "$HOME/bin", "'$HOME/bin'")]
    [InlineData(ShellKind.PowerShell, "`id`", "'`id`'")]
    [InlineData(ShellKind.PowerShell, "$(id)", "'$(id)'")]
    [InlineData(ShellKind.PowerShell, "hi!", "'hi!'")]
    [InlineData(ShellKind.PowerShell, "say \"hi\"", "'say \"hi\"'")]
    [InlineData(ShellKind.PowerShell, "it's", "'it''s'")]
    [InlineData(ShellKind.PowerShell, "it’s", "'it’’s'")]
    [InlineData(ShellKind.PowerShell, "a b", "'a b'")]
    [InlineData(ShellKind.PowerShell, "a,b", "'a,b'")]
    [InlineData(ShellKind.PowerShell, "one\ntwo", "'one\ntwo'")]
    [InlineData(ShellKind.PowerShell, "", "''")]
    [InlineData(ShellKind.PowerShell, @"C:\data\", @"'C:\data\'")]
    [InlineData(ShellKind.PowerShell, "@file", "'@file'")]
    [InlineData(ShellKind.PowerShell, "a@b", "a@b")]
    [InlineData(ShellKind.PowerShell, "$2y$10$abc", "'$2y$10$abc'")]
    public void Argument_with_shell_characters_is_single_quoted_so_the_shell_passes_it_unchanged(
        ShellKind shell,
        string argument,
        string expected)
    {
        var details = Details() with { Cmd = [argument] };

        Assert.Equal($"docker run -d --name web nginx:alpine {expected}", DockerRunCommand.Build(details, shell));
    }

    [Theory]
    [InlineData(ShellKind.Posix)]
    [InlineData(ShellKind.PowerShell)]
    public void Safe_characters_are_written_bare(ShellKind shell)
    {
        var details = Details() with { Env = ["PATH=/usr/local/bin:/usr/bin", "LEVEL=100%", "A_B-c.d+e@f=1"] };

        Assert.Equal(
            "docker run -d --name web -e PATH=/usr/local/bin:/usr/bin -e LEVEL=100% -e A_B-c.d+e@f=1 nginx:alpine",
            DockerRunCommand.Build(details, shell));
    }

    [Theory]
    [InlineData(ShellKind.Posix, @"'C:\My Data:/data'", @"'GREETING=it'\''s me'", "sh -c 'echo \"$HOME\" && sleep 1'")]
    [InlineData(ShellKind.PowerShell, @"'C:\My Data:/data'", "'GREETING=it''s me'", "sh '-c' 'echo \"$HOME\" && sleep 1'")]
    public void Option_values_mounts_and_commands_are_quoted_as_whole_arguments(
        ShellKind shell,
        string mount,
        string env,
        string command)
    {
        var details = Details() with
        {
            Env = ["GREETING=it's me", "EMPTY="],
            Mounts = [new MountDetails("bind", @"C:\My Data", "/data", "", true, null)],
            Cmd = ["sh", "-c", "echo \"$HOME\" && sleep 1"],
        };

        Assert.Equal(
            $"docker run -d --name web -v {mount} -e {env} -e EMPTY= nginx:alpine {command}",
            DockerRunCommand.Build(details, shell));
    }

    [Theory]
    [InlineData("-c", "'-c'")]
    [InlineData("--%", "'--%'")]
    [InlineData("-a.b", "'-a.b'")]
    [InlineData("--flag=value", "'--flag=value'")]
    [InlineData("-", "'-'")]
    [InlineData("a-b", "a-b")]
    public void PowerShell_quotes_a_value_that_starts_with_a_dash_so_it_is_not_read_as_a_parameter(string argument, string expected)
    {
        var details = Details() with { Cmd = ["sh", argument] };

        Assert.Equal($"docker run -d --name web nginx:alpine sh {expected}", DockerRunCommand.Build(details, ShellKind.PowerShell));
    }

    [Fact]
    public void PowerShell_quotes_every_option_value_that_starts_with_a_dash_and_leaves_the_option_names_bare()
    {
        var details = Details("-web") with { Env = ["-X=1"], WorkingDir = "-w", User = "-u" };

        Assert.Equal(
            "docker run -d --name '-web' -e '-X=1' -w '-w' -u '-u' nginx:alpine",
            DockerRunCommand.Build(details, ShellKind.PowerShell));
    }

    [Fact]
    public void A_posix_shell_leaves_a_value_that_starts_with_a_dash_bare()
    {
        var details = Details() with { Cmd = ["sh", "-c", "--%"] };

        Assert.Equal("docker run -d --name web nginx:alpine sh -c --%", DockerRunCommand.Build(details, ShellKind.Posix));
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
            DockerRunCommand.Build(details, ShellKind.Posix));
    }
}

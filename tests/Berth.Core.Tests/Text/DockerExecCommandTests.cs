using Berth.Connections;
using Berth.Text;

namespace Berth.Tests.Text;

public sealed class DockerExecCommandTests
{
    private const string Id = "4f1c2a9b7d3e";

    private static readonly string[] Sh = ["sh"];

    private static ConnectionProfile Tcp(string host = "127.0.0.1", int port = 2375, TlsOptions? tls = null) =>
        new("c1", "Daily", TransportKind.Tcp, host, port, "", tls);

    private static ConnectionProfile Pipe(string host = ".", string pipe = "docker_engine") =>
        new("c2", "Desktop", TransportKind.NamedPipe, host, 0, pipe, null);

    // One per connection kind: the connection is always named, so DOCKER_HOST and the CLI's context do not matter.

    [Fact]
    public void Tcp_names_the_engine_with_H()
    {
        Assert.Equal<string>(["-H", "tcp://127.0.0.1:2375", "exec", "-it", Id, "sh"], DockerExecCommand.Arguments(Tcp(), Id, Sh));
    }

    [Fact]
    public void Tls_with_a_ca_only_verifies_with_that_ca()
    {
        var connection = Tcp("engine.lan", 2376, new TlsOptions(@"C:\certs\ca.pem", null, null));

        Assert.Equal<string>(
            ["--tlsverify", "--tlscacert", @"C:\certs\ca.pem", "-H", "tcp://engine.lan:2376", "exec", "-it", Id, "sh"],
            DockerExecCommand.Arguments(connection, Id, Sh));
    }

    [Fact]
    public void Tls_with_a_client_certificate_passes_the_certificate_and_its_key()
    {
        var connection = Tcp("engine.lan", 2376, new TlsOptions(@"C:\certs\ca.pem", @"C:\certs\cert.pem", @"C:\certs\key.pem"));

        Assert.Equal<string>(
            [
                "--tlsverify", "--tlscacert", @"C:\certs\ca.pem", "--tlscert", @"C:\certs\cert.pem", "--tlskey", @"C:\certs\key.pem",
                "-H", "tcp://engine.lan:2376", "exec", "-it", Id, "sh",
            ],
            DockerExecCommand.Arguments(connection, Id, Sh));
    }

    [Fact]
    public void A_named_pipe_on_this_machine_is_named_with_a_dot()
    {
        Assert.Equal<string>(["-H", "npipe:////./pipe/docker_engine", "exec", "-it", Id, "sh"], DockerExecCommand.Arguments(Pipe(), Id, Sh));
        Assert.Equal<string>(
            ["-H", "npipe:////./pipe/dockerDesktopLinuxEngine", "exec", "-it", Id, "sh"],
            DockerExecCommand.Arguments(Pipe(pipe: "dockerDesktopLinuxEngine"), Id, Sh));
    }

    [Fact]
    public void An_ipv6_host_is_bracketed()
    {
        Assert.Equal("tcp://[::1]:2375", DockerExecCommand.Arguments(Tcp("::1"), Id, Sh)[1]);
    }

    [Fact]
    public void A_pipe_on_another_machine_names_it()
    {
        Assert.Equal("npipe:////build01/pipe/docker_engine", DockerExecCommand.Arguments(Pipe("build01"), Id, Sh)[1]);
    }

    [Fact]
    public void The_command_follows_the_container()
    {
        var arguments = DockerExecCommand.Arguments(Tcp(), Id, ["sh", "-c", "echo a b; sleep 5"]);

        Assert.Equal<string>(["exec", "-it", Id, "sh", "-c", "echo a b; sleep 5"], arguments.Skip(2));
    }

    [Fact]
    public void A_connection_docker_cannot_be_pointed_at_is_not_available_and_says_so()
    {
        // SSH is not built yet (phase 7); any kind without a case here is refused the same way.
        var ssh = Tcp() with { Kind = (TransportKind)99 };

        Assert.Equal("Not available for this connection.", DockerExecCommand.Unavailable(ssh));
        Assert.Throws<ArgumentException>(() => DockerExecCommand.Arguments(ssh, Id, Sh));
        Assert.Throws<ArgumentException>(() => DockerExecCommand.Text(ssh, Id, Sh, ShellKind.PowerShell));
    }

    [Theory]
    [InlineData(@"C:\certs\cert.pem", null)]
    [InlineData(null, @"C:\certs\key.pem")]
    [InlineData("", @"C:\certs\key.pem")]
    [InlineData(@"C:\certs\cert.pem", "")]
    public void Half_a_client_certificate_is_refused_with_a_reason_not_dropped(string? cert, string? key)
    {
        var connection = Tcp("engine.lan", 2376, new TlsOptions(@"C:\certs\ca.pem", cert, key));

        Assert.Equal(
            "The connection has a client certificate without its key, or a key without its certificate.",
            DockerExecCommand.Unavailable(connection));
        Assert.Throws<ArgumentException>(() => DockerExecCommand.Arguments(connection, Id, Sh));
        Assert.Throws<ArgumentException>(() => DockerExecCommand.Text(connection, Id, Sh, ShellKind.PowerShell));
    }

    [Fact]
    public void Tcp_tls_and_pipes_are_available()
    {
        Assert.Null(DockerExecCommand.Unavailable(Tcp()));
        Assert.Null(DockerExecCommand.Unavailable(Tcp(tls: new TlsOptions("ca.pem", null, null))));
        Assert.Null(DockerExecCommand.Unavailable(Pipe()));
    }

    [Fact]
    public void An_empty_command_or_container_is_refused()
    {
        Assert.Throws<ArgumentException>(() => DockerExecCommand.Arguments(Tcp(), Id, []));
        Assert.Throws<ArgumentException>(() => DockerExecCommand.Arguments(Tcp(), "", Sh));
    }

    // Text: what "Copy command" puts on the clipboard.

    [Fact]
    public void Text_for_powershell_quotes_a_path_with_spaces()
    {
        var connection = Tcp("engine.lan", 2376, new TlsOptions(@"C:\Users\a b\ca.pem", null, null));

        Assert.Equal(
            $@"docker --tlsverify --tlscacert 'C:\Users\a b\ca.pem' -H tcp://engine.lan:2376 exec -it {Id} sh",
            DockerExecCommand.Text(connection, Id, Sh, ShellKind.PowerShell));
    }

    [Fact]
    public void Text_for_bash_quotes_a_single_quote()
    {
        Assert.Equal(
            $@"docker -H tcp://127.0.0.1:2375 exec -it {Id} sh -c 'echo it'\''s'",
            DockerExecCommand.Text(Tcp(), Id, ["sh", "-c", "echo it's"], ShellKind.Posix));
        Assert.Equal(
            $"docker -H tcp://127.0.0.1:2375 exec -it {Id} sh '-c' 'echo it''s'",
            DockerExecCommand.Text(Tcp(), Id, ["sh", "-c", "echo it's"], ShellKind.PowerShell));
    }

    [Fact]
    public void Text_leaves_dockers_own_words_bare()
    {
        var connection = Tcp("::1", 2376, new TlsOptions("ca.pem", "cert.pem", "key.pem"));

        // "-H", "--tlsverify" and "-it" would be quoted as values in PowerShell; they are docker's options, so they stay bare.
        Assert.Equal(
            $"docker --tlsverify --tlscacert ca.pem --tlscert cert.pem --tlskey key.pem -H 'tcp://[::1]:2376' exec -it {Id} bash",
            DockerExecCommand.Text(connection, Id, ["bash"], ShellKind.PowerShell));
    }

    [Fact]
    public void Text_quotes_a_value_that_holds_a_variable_or_a_semicolon()
    {
        Assert.Equal(
            $"docker -H tcp://127.0.0.1:2375 exec -it {Id} sh -c 'echo %PATH% $HOME; ls'",
            DockerExecCommand.Text(Tcp(), Id, ["sh", "-c", "echo %PATH% $HOME; ls"], ShellKind.Posix));
    }
}

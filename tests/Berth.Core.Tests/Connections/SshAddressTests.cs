using Berth.Connections;

namespace Berth.Tests.Connections;

public sealed class SshAddressTests
{
    [Theory]
    [InlineData("ssh://berth@127.0.0.1:2222", "berth", "127.0.0.1", 2222)]
    [InlineData("ssh://deploy@build-host", "deploy", "build-host", 22)]
    [InlineData("SSH://me@[::1]:2200/", "me", "::1", 2200)]
    [InlineData("  ssh://first.last@host.example  ", "first.last", "host.example", 22)]
    public void Parses_an_ssh_address(string text, string user, string host, int port)
    {
        var ok = EngineAddress.TryParse(text, out var address, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal(new ParsedAddress(TransportKind.Ssh, host, port, "", user), address);
    }

    [Theory]
    [InlineData("ssh://host", "Put the user name in the address, like ssh://user@host.")]
    [InlineData("ssh://@host", "Put the user name in the address, like ssh://user@host.")]
    [InlineData("ssh://user@", "The address has no host.")]
    [InlineData("ssh://user@host:0", "The port must be between 1 and 65535.")]
    [InlineData("ssh://user@host/var/run", "Use ssh://user@host or ssh://user@host:port.")]
    [InlineData("ssh://user@host:22?x", "The port must be between 1 and 65535.")]
    [InlineData("http://x", "Use tcp://, npipe:// or ssh://.")]
    [InlineData("ssh://user:secret@host", "Do not put a password in the address: Berth asks for it and keeps it in Windows Credential Manager.")]
    [InlineData("ssh://user:@host:22", "Do not put a password in the address: Berth asks for it and keeps it in Windows Credential Manager.")]
    [InlineData("ssh://us er@host", "The user name cannot hold spaces or control characters.")]
    public void Rejects_a_wrong_ssh_address(string text, string expected)
    {
        var ok = EngineAddress.TryParse(text, out _, out var error);

        Assert.False(ok);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void The_six_part_parse_reads_an_ssh_address_without_its_user()
    {
        var ok = EngineAddress.TryParse("ssh://berth@host:2222", out var kind, out var host, out var port, out var pipe, out _);

        Assert.True(ok);
        Assert.Equal((TransportKind.Ssh, "host", 2222, ""), (kind, host, port, pipe));
    }

    [Fact]
    public void Formats_an_ssh_profile_as_docker_host_writes_it()
    {
        var profile = SshProfile("::1", 2222, "berth");

        Assert.Equal("ssh://berth@[::1]:2222", EngineAddress.Format(profile));
        Assert.Equal("ssh://berth@host:22", EngineAddress.Format(SshProfile("host", 22, "berth")));
    }

    internal static ConnectionProfile SshProfile(string host, int port, string user, SshSignIn signIn = SshSignIn.Password, string? keyPath = null) =>
        new("id", "Remote", TransportKind.Ssh, host, port, "", null) { Ssh = new SshOptions(user, signIn, keyPath, []) };
}

using Berth.Connections;
using Berth.Settings;

namespace Berth.Tests.Settings;

public sealed class SettingsStoreSshTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "berth-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreSshTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    private static readonly HostKey Key = new("host", 2222, "ssh-ed25519", "SHA256:Q90nzWlu3QhV+ZrycI9H0M/QxcWAxT4EJxSwFm9nlxc");

    private const string Ssh = """{ "id": "one", "name": "Remote", "kind": "Ssh", "host": "host" """;

    private AppSettings? LoadFrom(string json)
    {
        File.WriteAllText(SettingsFile, json);
        var load = new SettingsStore(_directory, null).Load();
        return load.SetAside ? null : load.Settings;
    }

    [Fact]
    public void An_ssh_connection_round_trips_with_its_saved_host_keys_and_no_secret()
    {
        var store = new SettingsStore(_directory, null);
        var profile = new ConnectionProfile("one", "Remote", TransportKind.Ssh, "host", 2222, "", null)
        {
            Ssh = new SshOptions("berth", SshSignIn.KeyFile, @"C:\keys\id_ed25519", [Key]),
        };

        store.Save(new AppSettings([profile], "one", ContainerListOptions.Default));
        var loaded = new SettingsStore(_directory, null).Load().Settings;

        Assert.Equal(profile, Assert.Single(loaded.Connections));
        var json = File.ReadAllText(SettingsFile);
        Assert.Contains("\"knownHosts\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passphrase", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_fields_read_as_port_22_password_sign_in_and_no_keys()
    {
        var settings = LoadFrom($$"""{ "connections": [ {{Ssh}}, "ssh": { "user": "berth" } } ] }""");

        var connection = Assert.Single(settings!.Connections);
        Assert.Equal(22, connection.Port);
        Assert.Equal(new SshOptions("berth", SshSignIn.Password, null, []), connection.Ssh);
    }

    [Theory]
    [InlineData("")]
    [InlineData(""", "ssh": null""")]
    [InlineData(""", "ssh": { "user": " " }""")]
    [InlineData(""", "ssh": { "user": "berth:not-a-real-secret" }""")]
    [InlineData(""", "ssh": { "user": "berth", "signIn": "KeyFile" }""")]
    public void An_ssh_connection_without_a_user_or_its_key_file_is_not_usable(string ssh) =>
        Assert.Null(LoadFrom($$"""{ "connections": [ {{Ssh}}{{ssh}} } ] }"""));

    [Fact]
    public void A_saved_host_key_that_is_not_whole_is_dropped_and_the_others_kept()
    {
        var settings = LoadFrom($$"""
            { "connections": [ {{Ssh}}, "port": 2222, "ssh": { "user": "berth", "knownHosts": [
              { "host": "host", "port": 2222, "type": "ssh-ed25519", "fingerprint": "SHA256:Q90nzWlu3QhV+ZrycI9H0M/QxcWAxT4EJxSwFm9nlxc" },
              { "host": "host", "port": 2222, "type": "ssh-ed25519" },
              { "host": "host", "port": 0, "type": "ssh-rsa", "fingerprint": "SHA256:x" },
              null ] } } ] }
            """);

        Assert.Equal<HostKey>([Key], Assert.Single(settings!.Connections).Ssh!.KnownHosts);
    }

    [Fact]
    public void Engine_control_saved_on_an_ssh_connection_is_dropped()
    {
        var settings = LoadFrom($$"""
            { "connections": [ {{Ssh}}, "ssh": { "user": "berth" }, "engineControl": { "startCommand": "a.exe", "stopCommand": "b.exe" } } ] }
            """);

        Assert.Null(Assert.Single(settings!.Connections).EngineControl);
    }

    [Fact]
    public void Docker_host_over_ssh_gives_an_ssh_connection_named_by_its_host()
    {
        var connection = Assert.Single(FirstRun.Create("ssh://berth@127.0.0.1:2222").Connections);

        Assert.Equal(TransportKind.Ssh, connection.Kind);
        Assert.Equal("127.0.0.1", connection.Name);
        Assert.Equal(("127.0.0.1", 2222), (connection.Host, connection.Port));
        Assert.Equal(new SshOptions("berth", SshSignIn.Password, null, []), connection.Ssh);
    }

    [Fact]
    public void Docker_host_over_ssh_with_a_password_in_it_creates_no_ssh_connection_and_keeps_nothing_of_it()
    {
        var settings = FirstRun.Create("ssh://berth:not-a-real-secret@host:2222");

        var connection = Assert.Single(settings.Connections);
        Assert.Equal(TransportKind.NamedPipe, connection.Kind);
        new SettingsStore(_directory, null).Save(settings);
        Assert.DoesNotContain("not-a-real-secret", File.ReadAllText(SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Docker_host_over_ssh_without_a_user_falls_back_to_the_default_pipe() =>
        Assert.Equal(TransportKind.NamedPipe, Assert.Single(FirstRun.Create("ssh://host").Connections).Kind);
}

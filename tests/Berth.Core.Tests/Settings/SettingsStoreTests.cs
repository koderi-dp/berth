using Berth.Connections;
using Berth.Settings;

namespace Berth.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "berth-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    [Fact]
    public void Missing_file_loads_first_run_defaults()
    {
        var settings = new SettingsStore(_directory, "tcp://127.0.0.1:2375").Load();

        var connection = Assert.Single(settings.Connections);
        Assert.Equal("Docker (tcp://127.0.0.1:2375)", connection.Name);
        Assert.Equal(connection.Id, settings.ActiveConnectionId);
        Assert.False(settings.ContainerList.RunningOnly);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var store = new SettingsStore(_directory, null);
        var saved = new AppSettings(
            [
                new ConnectionProfile("one", "Build host", TransportKind.Tcp, "build-host", 2376, "", new TlsOptions(@"C:\certs\ca.pem", @"C:\certs\cert.pem", null)),
                new ConnectionProfile("two", "Local engine", TransportKind.NamedPipe, ".", 0, "docker_engine", null),
            ],
            "two",
            new ContainerListOptions(RunningOnly: true));

        store.Save(saved);
        var loaded = new SettingsStore(_directory, null).Load();

        Assert.Equal(saved.Connections, loaded.Connections);
        Assert.Equal("two", loaded.ActiveConnectionId);
        Assert.Equal(saved.ContainerList, loaded.ContainerList);

        var json = File.ReadAllText(SettingsFile);
        Assert.Contains("\"activeConnectionId\"", json);
        Assert.Contains("\"NamedPipe\"", json);
        Assert.Contains("\n", json);
    }

    [Fact]
    public void Corrupt_file_loads_defaults_and_is_kept_as_settings_bad_json()
    {
        File.WriteAllText(SettingsFile, "{ this is not json");

        var settings = new SettingsStore(_directory, null).Load();

        var connection = Assert.Single(settings.Connections);
        Assert.Equal("Local engine", connection.Name);
        Assert.False(File.Exists(SettingsFile));
        Assert.Equal("{ this is not json", File.ReadAllText(Path.Combine(_directory, "settings.bad.json")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{ "connections": [ { "id": "x" } ], "activeConnectionId": "x", "containerList": { "runningOnly": false } }""")]
    [InlineData("""{ "connections": [ null ], "activeConnectionId": null, "containerList": { "runningOnly": false } }""")]
    [InlineData("""{ "connections": [], "activeConnectionId": null, "containerList": { "runningOnly": false } }""")]
    public void Json_that_is_not_valid_settings_is_treated_as_corrupt(string json)
    {
        File.WriteAllText(SettingsFile, json);

        var settings = new SettingsStore(_directory, null).Load();

        Assert.Equal("Local engine", Assert.Single(settings.Connections).Name);
        Assert.True(File.Exists(Path.Combine(_directory, "settings.bad.json")));
    }

    [Fact]
    public void File_starting_with_a_byte_order_mark_loads_its_connections()
    {
        const string json = """{ "connections": [ { "id": "one", "name": "Build host", "kind": "Tcp", "host": "build-host", "port": 2375, "pipeName": "", "tls": null } ], "activeConnectionId": "one", "containerList": { "runningOnly": true } }""";
        File.WriteAllBytes(SettingsFile, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(json)]);

        var settings = new SettingsStore(_directory, null).Load();

        var connection = Assert.Single(settings.Connections);
        Assert.Equal("Build host", connection.Name);
        Assert.Equal("one", settings.ActiveConnectionId);
        Assert.True(settings.ContainerList.RunningOnly);
        Assert.True(File.Exists(SettingsFile));
        Assert.False(File.Exists(Path.Combine(_directory, "settings.bad.json")));
    }

    [Fact]
    public void Active_id_pointing_at_no_connection_falls_back_to_the_first()
    {
        var store = new SettingsStore(_directory, null);
        store.Save(new AppSettings(
            [
                new ConnectionProfile("first", "First", TransportKind.Tcp, "a", 2375, "", null),
                new ConnectionProfile("second", "Second", TransportKind.Tcp, "b", 2375, "", null),
            ],
            "gone",
            new ContainerListOptions(RunningOnly: false)));

        var settings = store.Load();

        Assert.Equal("first", settings.ActiveConnectionId);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var store = new SettingsStore(_directory, null);

        store.Save(store.Load());
        store.Save(store.Load());

        var file = Assert.Single(Directory.GetFiles(_directory));
        Assert.Equal("settings.json", Path.GetFileName(file));
    }
}

using Berth.Connections;
using Berth.Settings;

namespace Berth.Tests.Settings;

public sealed class SettingsStoreEngineControlTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "berth-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreEngineControlTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    private const string Connection = """{ "id": "one", "name": "Local", "kind": "Tcp", "host": "127.0.0.1" """;

    private AppSettings LoadFrom(string json)
    {
        File.WriteAllText(SettingsFile, json);
        var load = new SettingsStore(_directory, null).Load();
        Assert.False(load.SetAside);
        return load.Settings;
    }

    [Fact]
    public void Engine_control_and_the_tray_options_round_trip()
    {
        var store = new SettingsStore(_directory, null);
        var saved = new AppSettings(
            [new ConnectionProfile("one", "Local", TransportKind.Tcp, "127.0.0.1", 2375, "", null) { EngineControl = EngineControl.WslDefault("Ubuntu-24.04") }],
            "one",
            ContainerListOptions.Default)
        {
            Tray = new TrayOptions(KeepRunningWhenClosed: false, StillRunningNoticeShown: true),
        };

        store.Save(saved);
        var loaded = new SettingsStore(_directory, null).Load().Settings;

        Assert.Equal(saved.Connections, loaded.Connections);
        Assert.Equal(saved.Tray, loaded.Tray);
        Assert.Contains("\"engineControl\"", File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void A_file_from_before_engine_control_reads_with_it_off_and_the_tray_defaults()
    {
        var settings = LoadFrom($$"""{ "connections": [ {{Connection}} } ] }""");

        Assert.Null(Assert.Single(settings.Connections).EngineControl);
        Assert.Equal(TrayOptions.Default, settings.Tray);
        Assert.True(settings.Tray.KeepRunningWhenClosed);
        Assert.False(settings.Tray.StillRunningNoticeShown);
    }

    [Theory]
    [InlineData("""{ "startCommand": "wsl.exe start" }""")]
    [InlineData("""{ "stopCommand": "wsl.exe stop" }""")]
    [InlineData("""{ "startCommand": " ", "stopCommand": "wsl.exe stop" }""")]
    [InlineData("""{ "startCommand": "\"open", "stopCommand": "wsl.exe stop" }""")]
    [InlineData("null")]
    public void Engine_control_that_is_not_whole_is_dropped_and_the_connection_kept(string engineControl)
    {
        var settings = LoadFrom($$"""{ "connections": [ {{Connection}}, "engineControl": {{engineControl}} } ] }""");

        var connection = Assert.Single(settings.Connections);
        Assert.Equal("Local", connection.Name);
        Assert.Null(connection.EngineControl);
    }

    [Fact]
    public void Missing_tray_fields_read_as_their_defaults()
    {
        var settings = LoadFrom($$"""{ "connections": [ {{Connection}} } ], "tray": { "stillRunningNoticeShown": true } }""");

        Assert.Equal(new TrayOptions(KeepRunningWhenClosed: true, StillRunningNoticeShown: true), settings.Tray);
    }
}

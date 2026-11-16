using Berth.Connections;
using Berth.Presentation;
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
        var settings = new SettingsStore(_directory, "tcp://127.0.0.1:2375").Load().Settings;

        var connection = Assert.Single(settings.Connections);
        Assert.Equal("Local engine", connection.Name);
        Assert.Equal(connection.Id, settings.ActiveConnectionId);
        Assert.False(settings.ContainerList.RunningOnly);
    }

    [Fact]
    public void A_saved_connection_keeps_the_name_an_older_first_run_gave_it()
    {
        var old = new ConnectionProfile("one", "Docker (tcp://127.0.0.1:2375)", TransportKind.Tcp, "127.0.0.1", 2375, "", null);
        new SettingsStore(_directory, null).Save(new AppSettings([old], "one", new ContainerListOptions(RunningOnly: false, ContainerSort.Default)));

        var settings = new SettingsStore(_directory, "tcp://127.0.0.1:2375").Load().Settings;

        Assert.Equal("Docker (tcp://127.0.0.1:2375)", Assert.Single(settings.Connections).Name);
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
            new ContainerListOptions(RunningOnly: true, new ContainerSort(SortColumn.Created, Descending: true)));

        store.Save(saved);
        var loaded = new SettingsStore(_directory, null).Load().Settings;

        Assert.Equal(saved.Connections, loaded.Connections);
        Assert.Equal("two", loaded.ActiveConnectionId);
        Assert.Equal(saved.ContainerList, loaded.ContainerList);

        var json = File.ReadAllText(SettingsFile);
        Assert.Contains("\"activeConnectionId\"", json);
        Assert.Contains("\"NamedPipe\"", json);
        Assert.Contains("\"Created\"", json);
        Assert.Contains("\n", json);
    }

    [Theory]
    [InlineData("""{ "runningOnly": true }""", SortColumn.Name, false)]
    [InlineData("""{ "runningOnly": true, "sort": { "column": "Status" } }""", SortColumn.Status, false)]
    [InlineData("""{ "runningOnly": true, "sort": { "descending": true } }""", SortColumn.Name, true)]
    [InlineData("""{ "runningOnly": true, "sort": { "column": "Ports", "descending": true } }""", SortColumn.Name, true)]
    [InlineData("""{ "runningOnly": true, "sort": null }""", SortColumn.Name, false)]
    public void A_missing_or_unknown_sort_reads_as_the_default_and_keeps_the_rest(string containerList, SortColumn column, bool descending)
    {
        File.WriteAllText(SettingsFile, $$"""{ "connections": [ { "id": "one", "name": "Build host", "kind": "Tcp", "host": "build-host" } ], "containerList": {{containerList}} }""");

        var result = new SettingsStore(_directory, null).Load();

        Assert.False(result.SetAside);
        Assert.Equal(new ContainerListOptions(RunningOnly: true, new ContainerSort(column, descending)), result.Settings.ContainerList);
    }

    [Fact]
    public void Log_view_options_round_trip()
    {
        var store = new SettingsStore(_directory, null);
        var saved = store.Load().Settings with { Logs = new LogViewOptions(Timestamps: true, Wrap: false) };

        store.Save(saved);
        var loaded = new SettingsStore(_directory, null).Load().Settings;

        Assert.Equal(new LogViewOptions(Timestamps: true, Wrap: false), loaded.Logs);
        Assert.Contains("\"logs\"", File.ReadAllText(SettingsFile));
    }

    [Theory]
    [InlineData("", false, true)]
    [InlineData(""", "logs": null""", false, true)]
    [InlineData(""", "logs": { "timestamps": true }""", true, true)]
    [InlineData(""", "logs": { "wrap": false }""", false, false)]
    public void Missing_log_view_options_read_as_the_defaults_and_keep_the_rest(string logs, bool timestamps, bool wrap)
    {
        File.WriteAllText(SettingsFile, $$"""{ "connections": [ { "id": "one", "name": "Build host", "kind": "Tcp", "host": "build-host" } ]{{logs}} }""");

        var result = new SettingsStore(_directory, null).Load();

        Assert.False(result.SetAside);
        Assert.Equal(new LogViewOptions(timestamps, wrap), result.Settings.Logs);
    }

    [Fact]
    public void Log_view_options_default_to_no_timestamps_and_wrapped_lines()
    {
        Assert.Equal(new LogViewOptions(Timestamps: false, Wrap: true), LogViewOptions.Default);
        Assert.Equal(LogViewOptions.Default, new SettingsStore(_directory, "tcp://127.0.0.1:2375").Load().Settings.Logs);
    }

    [Fact]
    public void Corrupt_file_loads_defaults_and_is_kept_as_settings_bad_json()
    {
        File.WriteAllText(SettingsFile, "{ this is not json");

        var settings = new SettingsStore(_directory, null).Load().Settings;

        var connection = Assert.Single(settings.Connections);
        Assert.Equal("Local engine", connection.Name);
        Assert.Equal("{ this is not json", File.ReadAllText(Path.Combine(_directory, "settings.bad.json")));
        Assert.Equal(connection.Id, new SettingsStore(_directory, null).Load().Settings.ActiveConnectionId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{ "connections": [ { "id": "x" } ], "activeConnectionId": "x", "containerList": { "runningOnly": false } }""")]
    [InlineData("""{ "connections": [ null ], "activeConnectionId": null, "containerList": { "runningOnly": false } }""")]
    [InlineData("""{ "connections": [], "activeConnectionId": null, "containerList": { "runningOnly": false } }""")]
    // Each connection below is complete except for the one thing that makes it unusable.
    [InlineData("""{ "connections": [ { "id": "x", "name": "X", "kind": 7, "host": "h", "port": 2375 } ] }""")]
    [InlineData("""{ "connections": [ { "id": "x", "name": "X", "kind": "Ssh", "host": "h", "port": 2375 } ] }""")]
    [InlineData("""{ "connections": [ { "id": "x", "name": "X", "host": "h", "port": 2375 } ] }""")]
    [InlineData("""{ "connections": [ { "id": "x", "name": "X", "kind": "Tcp", "host": "", "port": 2375 } ] }""")]
    [InlineData("""{ "connections": [ { "id": "x", "name": "X", "kind": "Tcp", "port": 2375 } ] }""")]
    [InlineData("""{ "connections": [ { "id": "x", "name": "X", "kind": "Tcp", "host": "h", "port": 70000 } ] }""")]
    [InlineData("""{ "connections": [ { "id": "x", "name": "X", "kind": "Tcp", "host": "h", "port": 0 } ] }""")]
    [InlineData("""{ "connections": [ { "id": " ", "name": "X", "kind": "Tcp", "host": "h", "port": 2375 } ] }""")]
    [InlineData("""{ "connections": [ { "name": "X", "kind": "Tcp", "host": "h", "port": 2375 } ] }""")]
    [InlineData("""{ "connections": [ { "id": "x", "name": "", "kind": "Tcp", "host": "h", "port": 2375 } ] }""")]
    [InlineData("""{ "connections": [ { "id": "x", "name": "X", "kind": "NamedPipe", "host": ".", "pipeName": " " } ] }""")]
    [InlineData("""{ "connections": [ { "id": "x", "name": "X", "kind": "NamedPipe", "host": "." } ] }""")]
    [InlineData("""{ "connections": [ { "id": "x", "name": "X", "kind": "Tcp", "host": "h", "port": 2376, "tls": { "certPath": "c.pem" } } ] }""")]
    [InlineData("""{ "activeConnectionId": "x" }""")]
    public void Json_that_is_not_valid_settings_is_treated_as_corrupt(string json)
    {
        File.WriteAllText(SettingsFile, json);

        var result = new SettingsStore(_directory, null).Load();

        Assert.Equal("Local engine", Assert.Single(result.Settings.Connections).Name);
        Assert.True(result.SetAside);
        Assert.Equal(json, File.ReadAllText(Path.Combine(_directory, "settings.bad.json")));
    }

    [Fact]
    public void Two_connections_with_the_same_id_are_treated_as_corrupt()
    {
        File.WriteAllText(SettingsFile, """
            { "connections": [
                { "id": "x", "name": "One", "kind": "Tcp", "host": "a", "port": 2375 },
                { "id": "x", "name": "Two", "kind": "Tcp", "host": "b", "port": 2375 } ] }
            """);

        var result = new SettingsStore(_directory, null).Load();

        Assert.True(result.SetAside);
        Assert.Equal("Local engine", Assert.Single(result.Settings.Connections).Name);
    }

    [Fact]
    public void A_minimal_hand_written_connection_loads()
    {
        File.WriteAllText(SettingsFile, """{ "connections": [ { "id": "one", "name": "Build host", "kind": "Tcp", "host": "build-host", "port": 2375 } ] }""");

        var result = new SettingsStore(_directory, null).Load();

        Assert.False(result.SetAside);
        Assert.Equal(new ConnectionProfile("one", "Build host", TransportKind.Tcp, "build-host", 2375, "", null), Assert.Single(result.Settings.Connections));
        Assert.Equal("one", result.Settings.ActiveConnectionId);
        Assert.False(result.Settings.ContainerList.RunningOnly);
        Assert.False(File.Exists(Path.Combine(_directory, "settings.bad.json")));
    }

    [Fact]
    public void A_file_without_container_list_options_loads_with_the_defaults()
    {
        File.WriteAllText(SettingsFile, """
            { "connections": [ { "id": "one", "name": "Local", "kind": "NamedPipe", "host": ".", "port": 0, "pipeName": "docker_engine", "tls": null } ],
              "activeConnectionId": "one" }
            """);

        var result = new SettingsStore(_directory, null).Load();

        Assert.False(result.SetAside);
        Assert.Equal("docker_engine", Assert.Single(result.Settings.Connections).PipeName);
        Assert.Equal(new ContainerListOptions(RunningOnly: false, ContainerSort.Default), result.Settings.ContainerList);
    }

    [Fact]
    public void A_missing_port_is_the_default_for_the_kind()
    {
        File.WriteAllText(SettingsFile, """
            { "connections": [
                { "id": "plain", "name": "Plain", "kind": "Tcp", "host": "a" },
                { "id": "tls", "name": "TLS", "kind": "Tcp", "host": "b", "tls": { "caPath": "ca.pem" } },
                { "id": "pipe", "name": "Pipe", "kind": "NamedPipe", "pipeName": "docker_engine" } ] }
            """);

        var connections = new SettingsStore(_directory, null).Load().Settings.Connections;

        Assert.Equal(2375, connections[0].Port);
        Assert.Equal(2376, connections[1].Port);
        Assert.Equal(new TlsOptions("ca.pem", null, null), connections[1].Tls);
        Assert.Equal(0, connections[2].Port);
        Assert.Equal(".", connections[2].Host);
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        File.WriteAllText(SettingsFile, """
            { "connections": [ { "id": "one", "name": "Local", "kind": "Tcp", "host": "h", "port": 2375, "sshUser": "me" } ],
              "theme": "dark", "containerList": { "runningOnly": true, "columns": [ "name" ] } }
            """);

        var result = new SettingsStore(_directory, null).Load();

        Assert.False(result.SetAside);
        Assert.Equal("one", Assert.Single(result.Settings.Connections).Id);
        Assert.True(result.Settings.ContainerList.RunningOnly);
    }

    [Fact]
    public void First_run_defaults_are_saved_so_the_connection_id_stays_the_same()
    {
        var first = new SettingsStore(_directory, "tcp://127.0.0.1:2375").Load();
        var second = new SettingsStore(_directory, null).Load();

        Assert.True(File.Exists(SettingsFile));
        Assert.False(first.SetAside);
        Assert.False(second.SetAside);
        Assert.Equal(first.Settings.Connections, second.Settings.Connections);
        Assert.Equal(first.Settings.ActiveConnectionId, second.Settings.ActiveConnectionId);
    }

    [Fact]
    public void A_first_run_save_that_fails_is_reported_and_the_defaults_are_still_used()
    {
        // A file where the folder should be: the folder cannot be created, so the save fails.
        var blocked = Path.Combine(_directory, "blocked");
        File.WriteAllText(blocked, "");

        var result = new SettingsStore(Path.Combine(blocked, "settings"), "tcp://127.0.0.1:2375").Load();

        Assert.NotNull(result.SaveError);
        Assert.False(result.SetAside);
        Assert.Equal("tcp://127.0.0.1:2375", EngineAddress.Format(Assert.Single(result.Settings.Connections)));
    }

    [Fact]
    public void A_valid_file_is_not_set_aside()
    {
        var store = new SettingsStore(_directory, null);
        store.Save(new AppSettings([new ConnectionProfile("one", "Local", TransportKind.Tcp, "h", 2375, "", null)], "one", new ContainerListOptions(false, ContainerSort.Default)));

        var result = store.Load();

        Assert.False(result.SetAside);
        Assert.Null(result.SaveError);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_left_alone_and_not_overwritten()
    {
        const string json = """{ "connections": [ { "id": "one", "name": "Local", "kind": "Tcp", "host": "h", "port": 2375 } ] }""";
        File.WriteAllText(SettingsFile, json);

        SettingsLoad result;
        using (new FileStream(SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = new SettingsStore(_directory, null).Load();
        }

        Assert.False(result.SetAside);
        Assert.Equal("Local engine", Assert.Single(result.Settings.Connections).Name);
        Assert.Equal(json, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void File_starting_with_a_byte_order_mark_loads_its_connections()
    {
        const string json = """{ "connections": [ { "id": "one", "name": "Build host", "kind": "Tcp", "host": "build-host", "port": 2375, "pipeName": "", "tls": null } ], "activeConnectionId": "one", "containerList": { "runningOnly": true } }""";
        File.WriteAllBytes(SettingsFile, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(json)]);

        var settings = new SettingsStore(_directory, null).Load().Settings;

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
            new ContainerListOptions(RunningOnly: false, ContainerSort.Default)));

        var settings = store.Load().Settings;

        Assert.Equal("first", settings.ActiveConnectionId);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var store = new SettingsStore(_directory, null);

        store.Save(store.Load().Settings);
        store.Save(store.Load().Settings);

        var file = Assert.Single(Directory.GetFiles(_directory));
        Assert.Equal("settings.json", Path.GetFileName(file));
    }
}

using Berth.Connections;
using Berth.Settings;
using Berth.State;

namespace Berth.Tests.Settings;

public sealed class ConnectionEditsTests
{
    private static readonly ConnectionProfile One = new("one", "One", TransportKind.Tcp, "127.0.0.1", 2375, "", null);
    private static readonly ConnectionProfile Two = new("two", "Two", TransportKind.Tcp, "127.0.0.1", 1, "", null);
    private static readonly ConnectionProfile Three = new("three", "Three", TransportKind.NamedPipe, ".", 0, "docker_engine", null);

    private static AppSettings With(string active, params ConnectionProfile[] connections) =>
        new(connections, active, new ContainerListOptions(RunningOnly: true, ContainerSort.Default));

    [Fact]
    public void Add_appends_the_connection_and_keeps_the_active_one()
    {
        var settings = ConnectionEdits.Add(With("one", One), Two);

        Assert.Equal([One, Two], settings.Connections);
        Assert.Equal("one", settings.ActiveConnectionId);
        Assert.True(settings.ContainerList.RunningOnly);
    }

    [Fact]
    public void Replace_swaps_the_connection_with_the_same_id_in_place()
    {
        var renamed = Two with { Name = "Renamed", Port = 2376 };

        var settings = ConnectionEdits.Replace(With("two", One, Two, Three), renamed);

        Assert.Equal([One, renamed, Three], settings.Connections);
        Assert.Equal("two", settings.ActiveConnectionId);
    }

    [Fact]
    public void Replace_with_an_unknown_id_changes_nothing()
    {
        var before = With("one", One);

        Assert.Same(before, ConnectionEdits.Replace(before, Two));
    }

    [Fact]
    public void Remove_drops_the_connection_and_keeps_the_active_one()
    {
        var settings = ConnectionEdits.Remove(With("one", One, Two, Three), "two");

        Assert.Equal([One, Three], settings.Connections);
        Assert.Equal("one", settings.ActiveConnectionId);
    }

    [Fact]
    public void Removing_the_active_connection_makes_the_first_remaining_one_active()
    {
        var settings = ConnectionEdits.Remove(With("one", One, Two, Three), "one");

        Assert.Equal([Two, Three], settings.Connections);
        Assert.Equal("two", settings.ActiveConnectionId);
    }

    [Fact]
    public void Removing_the_last_connection_changes_nothing()
    {
        var before = With("one", One);

        Assert.Same(before, ConnectionEdits.Remove(before, "one"));
    }

    [Fact]
    public void Removing_an_unknown_id_changes_nothing()
    {
        var before = With("one", One, Two);

        Assert.Same(before, ConnectionEdits.Remove(before, "nope"));
    }
}

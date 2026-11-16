using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;

namespace Berth.Tests.Presentation;

public sealed class TrayMenuTests
{
    private static ContainerSummary Container(string name, string state = "running") => new(
        "id-" + name, [$"/{name}"], "nginx:latest", "sha256:1", "nginx", 1_700_000_000, state, "Up", [], new Dictionary<string, string>());

    private static EngineState Connected(params ContainerSummary[] containers) =>
        EngineState.Initial with { Status = ConnectionStatus.Connected, Containers = [.. containers], ListLoaded = true };

    [Fact]
    public void Lists_the_running_containers_sorted_by_name()
    {
        var menu = TrayMenu.For("Local engine", Connected(Container("web"), Container("db", "exited"), Container("Api"), Container("cache")));

        Assert.Equal(["Api", "cache", "web"], menu.Containers.Select(c => c.Name));
        Assert.Equal("id-Api", menu.Containers[0].Id);
        Assert.Equal(0, menu.More);
        Assert.Null(menu.MoreText);
    }

    [Fact]
    public void Lists_at_most_fifteen_then_says_how_many_more()
    {
        var containers = Enumerable.Range(1, 18).Select(i => Container($"c{i:00}")).ToArray();

        var menu = TrayMenu.For("Local engine", Connected(containers));

        Assert.Equal(TrayMenu.MaxContainers, menu.Containers.Length);
        Assert.Equal("c15", menu.Containers[^1].Name);
        Assert.Equal(3, menu.More);
        Assert.Equal("and 3 more… Open Berth", menu.MoreText);
    }

    [Fact]
    public void A_container_with_an_action_under_way_is_busy()
    {
        var state = Connected(Container("web")) with { Pending = ImmutableDictionary<string, ContainerAction>.Empty.Add("id-web", ContainerAction.Stop) };

        Assert.True(TrayMenu.For("Local", state).Containers[0].Busy);
    }

    [Fact]
    public void No_containers_are_listed_while_not_connected()
    {
        var state = Connected(Container("web")) with { Status = ConnectionStatus.Disconnected, ConnectionError = "refused" };

        var menu = TrayMenu.For("Local", state);

        Assert.Empty(menu.Containers);
        Assert.Equal("Engine not reachable", menu.StateText);
        Assert.Equal(TrayIconKind.Down, menu.Icon);
    }

    [Fact]
    public void Carries_the_connection_its_state_and_the_engine_command()
    {
        var state = Connected() with { EngineControl = EngineControlState.Ready };

        var menu = TrayMenu.For("Local engine", state);

        Assert.Equal("Local engine", menu.ConnectionName);
        Assert.Equal("Engine running", menu.StateText);
        Assert.Equal(new EngineMenu(EngineCommand.Stop, Enabled: true), menu.Engine);
        Assert.Equal(TrayIconKind.Running, menu.Icon);
        Assert.Equal("Berth: Local engine, Engine running", menu.Tooltip);
    }

    [Fact]
    public void No_engine_command_without_engine_control()
    {
        Assert.Null(TrayMenu.For("Local", Connected()).Engine);
    }

    [Fact]
    public void The_icon_follows_the_phase()
    {
        var controlled = EngineState.Initial with { EngineControl = EngineControlState.Ready };

        Assert.Equal(TrayIconKind.Working, TrayMenu.For("L", EngineState.Initial with { Status = ConnectionStatus.Connecting }).Icon);
        Assert.Equal(TrayIconKind.Working, TrayMenu.For("L", controlled with { EngineControl = EngineControlState.Ready with { Running = EngineCommand.Start } }).Icon);
        Assert.Equal(TrayIconKind.Stopped, TrayMenu.For("L", controlled with { EngineControl = EngineControlState.Ready with { StoppedByBerth = true } }).Icon);
    }

    [Fact]
    public void The_tooltip_fits_the_notification_area_limit()
    {
        var menu = TrayMenu.For(new string('x', 300), Connected());

        Assert.True(menu.Tooltip.Length <= TrayMenu.MaxTooltip);
        Assert.EndsWith("…, Engine running", menu.Tooltip);
    }

    [Fact]
    public void Equal_states_give_equal_menus_so_the_menu_is_built_once()
    {
        var a = TrayMenu.For("Local", Connected(Container("web"), Container("db")));
        var b = TrayMenu.For("Local", Connected(Container("db"), Container("web")) with { Stats = EngineState.Initial.Stats });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, TrayMenu.For("Local", Connected(Container("web"))));
        Assert.NotEqual(a, TrayMenu.For("Other", Connected(Container("web"), Container("db"))));
    }
}

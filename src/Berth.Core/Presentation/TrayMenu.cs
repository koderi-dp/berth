using System.Collections.Immutable;
using Berth.Session;

namespace Berth.Presentation;

/// <summary>Which icon the tray shows.</summary>
public enum TrayIconKind { Running, Working, Down, Stopped }

/// <summary>A running container in the tray menu, with Stop and Restart.</summary>
/// <param name="Busy">An action is under way on it: its items are disabled.</param>
public sealed record TrayContainer(string Id, string Name, bool Busy);

/// <summary>
/// The tray icon and its menu, made from the state alone: Open Berth; the connection's name and state; the running containers (by name,
/// at most <see cref="MaxContainers"/>, then <see cref="MoreText"/>), each with Stop and Restart; Start engine or Stop engine when the
/// connection has engine control; Quit. Equal states give equal menus, so the tray rebuilds its menu only when this changes.
/// </summary>
public sealed record TrayMenu(
    string ConnectionName,
    string StateText,
    ImmutableArray<TrayContainer> Containers,
    int More,
    EngineMenu? Engine,
    TrayIconKind Icon,
    string Tooltip)
{
    public const int MaxContainers = 15;

    /// <summary>The notification area shows at most 127 characters of a tooltip.</summary>
    public const int MaxTooltip = 127;

    /// <summary>The line after the listed containers when there are more; null when all are listed.</summary>
    public string? MoreText => More > 0 ? $"and {More} more… Open Berth" : null;

    public static TrayMenu For(string connectionName, EngineState state)
    {
        var phase = EngineStatus.Phase(state);
        var stateText = EngineStatus.Text(phase);
        var running = state.Status == ConnectionStatus.Connected
            ? state.Containers
                .Where(container => container.State == "running")
                .Select(container => new TrayContainer(container.Id, ComposeGrouping.NameOf(container), state.Pending.ContainsKey(container.Id)))
                .OrderBy(container => container.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(container => container.Name, StringComparer.Ordinal)
                .ToList()
            : [];

        return new TrayMenu(
            connectionName,
            stateText,
            [.. running.Take(MaxContainers)],
            Math.Max(0, running.Count - MaxContainers),
            EngineStatus.Menu(state),
            IconOf(phase),
            TooltipFor(connectionName, stateText));
    }

    public bool Equals(TrayMenu? other) =>
        other is not null
        && ConnectionName == other.ConnectionName
        && StateText == other.StateText
        && Containers.SequenceEqual(other.Containers)
        && More == other.More
        && Equals(Engine, other.Engine)
        && Icon == other.Icon
        && Tooltip == other.Tooltip;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ConnectionName);
        hash.Add(StateText);
        foreach (var container in Containers)
            hash.Add(container);
        hash.Add(More);
        hash.Add(Engine);
        hash.Add(Icon);
        return hash.ToHashCode();
    }

    private static TrayIconKind IconOf(EnginePhase phase) => phase switch
    {
        EnginePhase.Running => TrayIconKind.Running,
        EnginePhase.NotReachable or EnginePhase.NeedsAttention => TrayIconKind.Down,
        EnginePhase.Stopped => TrayIconKind.Stopped,
        _ => TrayIconKind.Working,
    };

    /// <summary>"Berth: name, state", the name shortened with an ellipsis when the whole would not fit.</summary>
    private static string TooltipFor(string connectionName, string stateText)
    {
        const string prefix = "Berth: ";
        var tail = ", " + stateText;
        var room = MaxTooltip - prefix.Length - tail.Length;
        var name = connectionName.Length <= room ? connectionName : connectionName[..(room - 1)] + "…";
        return prefix + name + tail;
    }
}

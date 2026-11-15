using System.Globalization;
using Berth.Engine.Models;

namespace Berth.Presentation;

/// <summary>How a state is drawn, so it reads without relying on colour: a filled dot, a half-filled one, or an empty ring.</summary>
public enum StateShape
{
    /// <summary>Running.</summary>
    Filled,

    /// <summary>In between: paused, restarting, or a compose project of which only part runs.</summary>
    Half,

    /// <summary>Not running.</summary>
    Ring,
}

/// <summary>What the containers table and a container's detail page show about its state.</summary>
public static class ContainerStatus
{
    private const string ExitedPrefix = "Exited (";

    /// <summary>
    /// True for a container that ended badly: the engine's status says "Exited (n)" with n other than 0, or "Dead". A clean
    /// exit ("Exited (0)") is not a failure.
    /// </summary>
    public static bool IsFailedExit(string status)
    {
        if (status.StartsWith("Dead", StringComparison.Ordinal))
            return true;
        if (!status.StartsWith(ExitedPrefix, StringComparison.Ordinal))
            return false;

        var end = status.IndexOf(')', ExitedPrefix.Length);
        return end > 0
            && int.TryParse(status.AsSpan(ExitedPrefix.Length, end - ExitedPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            && code != 0;
    }

    /// <summary>The shape for one container.</summary>
    public static StateShape Shape(RowState state) => state switch
    {
        RowState.Running => StateShape.Filled,
        RowState.Paused or RowState.Restarting => StateShape.Half,
        _ => StateShape.Ring,
    };

    /// <summary>The shape for a compose project: filled when every container runs, a ring when none is active, half otherwise.</summary>
    public static StateShape GroupShape(IReadOnlyCollection<RowState> states)
    {
        if (states.Count == 0 || states.All(s => s == RowState.Stopped))
            return StateShape.Ring;

        return states.All(s => s == RowState.Running) ? StateShape.Filled : StateShape.Half;
    }

    /// <summary>
    /// The state line of a container's detail page. The state is the list's, which follows the engine's events; a running container
    /// adds since when it runs from its last read ("Running (52 minutes ago)"), when that read also found it running. A stopped one
    /// says what the list says ("Exited (137) 3 hours ago", "Created"), or "Stopped" when the list says nothing.
    /// </summary>
    public static string Headline(RowState state, string listStatus, ContainerStateDetails? inspected, DateTimeOffset now) => state switch
    {
        RowState.Running when inspected is { Running: true, StartedAt: { } startedAt } => $"Running ({AgeFormat.Ago(now, startedAt)})",
        RowState.Running => "Running",
        RowState.Paused => "Paused",
        RowState.Restarting => "Restarting",
        _ => string.IsNullOrWhiteSpace(listStatus) ? "Stopped" : listStatus,
    };
}

using Berth.Connections;
using Berth.State;

namespace Berth.Settings;

/// <summary>How the containers list is shown: whether only running containers are, and how it is sorted.</summary>
public sealed record ContainerListOptions(bool RunningOnly, ContainerSort Sort)
{
    /// <summary>Every container, sorted by name ascending.</summary>
    public static readonly ContainerListOptions Default = new(RunningOnly: false, ContainerSort.Default);
}

/// <summary>
/// How a container's log is shown: with each line's timestamp or not, long lines wrapped or scrolled sideways, and in the colours
/// its colour codes ask for or plain.
/// </summary>
public sealed record LogViewOptions(bool Timestamps, bool Wrap, bool Colors = true)
{
    /// <summary>No timestamps, long lines wrapped, in colour.</summary>
    public static readonly LogViewOptions Default = new(Timestamps: false, Wrap: true, Colors: true);
}

/// <summary>What closing the window does, and whether Berth has already said once that it is still running in the tray.</summary>
/// <param name="KeepRunningWhenClosed">Closing the window hides it to the tray instead of quitting.</param>
/// <param name="StillRunningNoticeShown">The one-time notification that Berth is still running in the tray was shown.</param>
public sealed record TrayOptions(bool KeepRunningWhenClosed, bool StillRunningNoticeShown)
{
    /// <summary>Keep running in the tray; the notice not shown yet.</summary>
    public static readonly TrayOptions Default = new(KeepRunningWhenClosed: true, StillRunningNoticeShown: false);
}

/// <summary>Everything in <c>settings.json</c>: the saved connections, which one is active, the containers list, log and tray options.</summary>
public sealed record AppSettings(IReadOnlyList<ConnectionProfile> Connections, string? ActiveConnectionId, ContainerListOptions ContainerList)
{
    /// <summary>How the detail page's Logs tab shows a log.</summary>
    public LogViewOptions Logs { get; init; } = LogViewOptions.Default;

    /// <summary>What closing the window does.</summary>
    public TrayOptions Tray { get; init; } = TrayOptions.Default;
}

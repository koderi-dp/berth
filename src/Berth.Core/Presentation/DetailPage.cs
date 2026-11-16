using Berth.Session;

namespace Berth.Presentation;

/// <summary>What a container's detail page does next about its detail session.</summary>
public enum DetailPageStep
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Open a detail session for the container.</summary>
    Open,

    /// <summary>Close the session: its connection was lost (a new one opens on the next connection).</summary>
    Close,

    /// <summary>Have the session read the container again.</summary>
    Refresh,

    /// <summary>Go back to the list, which says the container was removed.</summary>
    Leave,
}

/// <summary>What the detail page knows when it decides its next step.</summary>
/// <param name="Active">The page is the one shown; a page kept in the navigation cache does nothing.</param>
/// <param name="Connected">The engine session is connected.</param>
/// <param name="ListLoaded">The connection's first container list has arrived.</param>
/// <param name="ListReloaded">The list was read again since the page last decided.</param>
/// <param name="PreviousRow">The container's row in the list read before, or null (not in it, or no list seen yet).</param>
/// <param name="Row">The container's row in the list now, or null when the list does not have it.</param>
/// <param name="Session">The state of the detail session open for this container, or null when none is.</param>
public sealed record DetailPageFacts(
    bool Active,
    bool Connected,
    bool ListLoaded,
    bool ListReloaded,
    ContainerRow? PreviousRow,
    ContainerRow? Row,
    DetailState? Session);

/// <summary>
/// The decisions of a container's detail page: when it opens, closes, refreshes or leaves its detail session, and which detail state
/// its tabs show while a session is closed or reopening.
/// </summary>
public static class DetailPage
{
    /// <summary>What a tab says in place of a stream's state while the page waits for its engine.</summary>
    public const string WaitingText = "Waiting for the engine…";

    /// <summary>
    /// The page's next step. Only the page shown acts. A container that is gone (the session's read answered 404, or a loaded list
    /// of a connected engine does not have it) leaves. A session whose connection was lost closes; with none open, one opens once
    /// connected. With one open, each list load that shows the container may have changed run refreshes it, unless an action on it
    /// is still running (its end is such a change).
    /// </summary>
    public static DetailPageStep Next(DetailPageFacts facts)
    {
        if (!facts.Active)
            return DetailPageStep.None;
        if (facts.Session is { Removed: true } || (facts.Connected && facts.ListLoaded && facts.Row is null))
            return DetailPageStep.Leave;
        if (facts.Session is { ConnectionLost: true })
            return DetailPageStep.Close;
        if (facts.Session is not { } session)
            return facts.Connected ? DetailPageStep.Open : DetailPageStep.None;

        return facts.Connected && facts.ListReloaded && facts.Row is { Pending: null } row
            && (Changed(facts.PreviousRow, row) || OutOfStep(row, session))
            ? DetailPageStep.Refresh
            : DetailPageStep.None;
    }

    /// <summary>The last live state: <paramref name="session"/> once it has read the container on a connection it still has.</summary>
    public static DetailState? KeepLive(DetailState? session, DetailState? lastLive) => IsLive(session) ? session : lastLive;

    /// <summary>
    /// The state the tabs show: the session's while it is live; otherwise (closed, its connection lost, or reopened and not read
    /// yet) the last live state of the same container, so the tabs stay mounted with what they showed; otherwise the session's.
    /// A state whose connection was lost is never shown when a live one is kept: its streams read as ended, which says the
    /// container stopped.
    /// </summary>
    public static DetailState? Shown(DetailState? session, DetailState? lastLive, string containerId) =>
        IsLive(session) ? session
        : lastLive is not null && lastLive.ContainerId == containerId ? lastLive
        : session;

    /// <summary>
    /// The tabs show <paramref name="shown"/> while waiting for the engine: it is not the live session's own state, or the engine
    /// is not connected. Their stream texts and Retry then give way to <see cref="WaitingText"/>.
    /// </summary>
    public static bool Waiting(DetailState? shown, DetailState? session, bool connected) =>
        shown is not null && (!connected || !ReferenceEquals(shown, session));

    private static bool IsLive(DetailState? session) => session is { Details: not null, ConnectionLost: false };

    /// <summary>The list shows the container differently: another state, status text ("Up 5 minutes" to "Up 1 second") or action.</summary>
    private static bool Changed(ContainerRow? previous, ContainerRow row) =>
        previous is not null && (previous.State != row.State || previous.Status != row.Status || previous.Pending != row.Pending);

    /// <summary>
    /// The session's last read or streams do not match the row: the list says running while the session read it stopped or
    /// restarting, or its streams ended (a restart it saw only half of); or the list says stopped while it read it running.
    /// </summary>
    private static bool OutOfStep(ContainerRow row, DetailState session) =>
        session.Details is { } details
        && row.State switch
        {
            RowState.Running => !details.State.Running || details.State.Restarting
                || session.LogStatus == StreamStatus.Ended || session.StatsStatus is StreamStatus.Ended or StreamStatus.Idle,
            RowState.Stopped => details.State.Running,
            _ => false,
        };
}

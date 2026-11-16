using System.Collections.Immutable;
using Berth.Session;

namespace Berth.Presentation;

/// <summary>Everything a row of the containers table, or the header of a container's detail page, can do.</summary>
public enum RowActionKind { ViewDetails, Start, Stop, Restart, Pause, Resume, OpenInBrowser, CopyName, CopyId, CopyRunPowerShell, CopyRunBash, Delete }

/// <summary>When an action is a button on its row; when it is not, it is in the row's "more" menu.</summary>
public enum ButtonWhen
{
    /// <summary>At every width: the row's main action and Delete.</summary>
    Always,

    /// <summary>In a wide window; in a narrow one it moves into the "more" menu.</summary>
    Wide,

    /// <summary>Never: always in the "more" menu.</summary>
    Never,
}

/// <summary>One action of a row.</summary>
/// <param name="IsMain">The row's main action (Start or Stop), drawn in the accent colour.</param>
/// <param name="EngineAction">What it sends to the engine, or null for an action that does not (copying, opening, and Delete, which is confirmed first).</param>
/// <param name="Ids">The containers it acts on.</param>
public sealed record RowAction(
    RowActionKind Kind,
    string Label,
    bool IsEnabled,
    ButtonWhen Button,
    bool IsMain,
    ContainerAction? EngineAction,
    ImmutableArray<string> Ids)
{
    /// <summary>More about the action, for its tooltip in a menu; null when the label says it all.</summary>
    public string? Description { get; init; }

    /// <summary>The shell a "Copy docker run" action writes the command for; null for any other action.</summary>
    public ShellKind? RunCommandShell => Kind switch
    {
        RowActionKind.CopyRunPowerShell => ShellKind.PowerShell,
        RowActionKind.CopyRunBash => ShellKind.Posix,
        _ => null,
    };
}

/// <summary>
/// The actions of one row of the containers table, from which the view draws its buttons, its "more" menu and its context menu,
/// so the three always agree on which actions there are, their labels and whether they are enabled. An engine action is enabled
/// while the row can act (<see cref="BulkSelection.CanAct(ContainerRow, bool)"/>); copying and opening always are.
/// </summary>
/// <param name="Sections">Every action in context menu order, in the groups a menu separates.</param>
/// <param name="BrowserUrl">The first published port's address, which Open in browser opens; null when there is none.</param>
public sealed record RowActions(IReadOnlyList<IReadOnlyList<RowAction>> Sections, Uri? BrowserUrl)
{
    /// <summary>
    /// The PowerShell command is quoted for PowerShell 7. Windows PowerShell 5.1 passes arguments to programs by older rules and loses a
    /// double quote inside a value.
    /// </summary>
    private const string PowerShellNote = "For PowerShell 7. Windows PowerShell 5.1 drops double quotes inside values.";

    /// <summary>Delete: always the last action, a button set apart from the others.</summary>
    public RowAction Delete => Sections[^1].Single(a => a.Kind == RowActionKind.Delete);

    /// <summary>The row's buttons before Delete, in order: those shown at every width, and in a wide window also the others that have one.</summary>
    public IReadOnlyList<RowAction> Buttons(bool compact) =>
        [.. Others.Where(a => IsButton(a, compact))];

    /// <summary>What the "more" menu holds: every action other than Delete that is not a button, in sections; empty when there is none.</summary>
    public IReadOnlyList<IReadOnlyList<RowAction>> MoreMenu(bool compact) =>
    [
        .. Sections
            .Select(s => (IReadOnlyList<RowAction>)[.. s.Where(a => a.Kind != RowActionKind.Delete && !IsButton(a, compact))])
            .Where(s => s.Count > 0),
    ];

    private IEnumerable<RowAction> Others => Sections.SelectMany(s => s).Where(a => a.Kind != RowActionKind.Delete);

    private static bool IsButton(RowAction action, bool compact) =>
        action.Button == ButtonWhen.Always || (action.Button == ButtonWhen.Wide && !compact);

    /// <summary>
    /// A container: View details (on its own, first, always enabled); then Start (stopped) or Stop (active), Restart, Pause (enabled
    /// only while running) or Resume (paused); then Open in browser (only with a published port), Copy name, Copy ID, and Copy docker
    /// run for PowerShell and for bash (enabled while connected, since the list has to read the container first); then Delete.
    /// </summary>
    /// <param name="viewDetails">False on the container's detail page, which has the same actions but View details.</param>
    public static RowActions ForContainer(ContainerRow row, bool connected, bool viewDetails = true)
    {
        var canAct = BulkSelection.CanAct(row, connected);
        ImmutableArray<string> ids = [row.Id];
        var paused = row.State == RowState.Paused;
        var browserUrl = row.Ports.FirstOrDefault(p => p.Url is not null)?.Url;

        RowAction Engine(RowActionKind kind, string label, ContainerAction action, ButtonWhen button, bool enabled = true, bool main = false) =>
            new(kind, label, canAct && enabled, button, main, action, ids);

        RowAction Local(RowActionKind kind, string label, bool enabled = true) =>
            new(kind, label, enabled, ButtonWhen.Never, IsMain: false, EngineAction: null, ids);

        RowAction[] run =
        [
            row.State == RowState.Stopped
                ? Engine(RowActionKind.Start, "Start", ContainerAction.Start, ButtonWhen.Always, main: true)
                : Engine(RowActionKind.Stop, "Stop", ContainerAction.Stop, ButtonWhen.Always, main: true),
            Engine(RowActionKind.Restart, "Restart", ContainerAction.Restart, ButtonWhen.Wide),
            paused
                ? Engine(RowActionKind.Resume, "Resume", ContainerAction.Unpause, ButtonWhen.Wide)
                : Engine(RowActionKind.Pause, "Pause", ContainerAction.Pause, ButtonWhen.Wide, enabled: row.State == RowState.Running),
        ];
        RowAction[] other =
        [
            .. browserUrl is null ? (RowAction[])[] : [Local(RowActionKind.OpenInBrowser, "Open in browser")],
            Local(RowActionKind.CopyName, "Copy name"),
            Local(RowActionKind.CopyId, "Copy ID"),
            Local(RowActionKind.CopyRunPowerShell, "Copy docker run (PowerShell)", enabled: connected) with { Description = PowerShellNote },
            Local(RowActionKind.CopyRunBash, "Copy docker run (bash)", enabled: connected),
        ];
        RowAction delete = new(RowActionKind.Delete, "Delete", canAct, ButtonWhen.Always, IsMain: false, EngineAction: null, ids);

        return viewDetails
            ? new RowActions([[Local(RowActionKind.ViewDetails, "View details")], run, other, [delete]], browserUrl)
            : new RowActions([run, other, [delete]], browserUrl);
    }

    /// <summary>
    /// A compose project's shown rows: Start for the stopped ones (when there are any), Stop for the others (when there are any),
    /// Restart for all; then Delete for all. Stop is the main action while anything is active, Start otherwise. Every action is
    /// enabled while connected and no action is in progress on any of the rows.
    /// </summary>
    public static RowActions ForProject(IReadOnlyList<ContainerRow> rows, bool connected)
    {
        var canAct = BulkSelection.CanAct(rows, connected);
        ImmutableArray<string> stopped = [.. rows.Where(r => r.State == RowState.Stopped).Select(r => r.Id)];
        ImmutableArray<string> active = [.. rows.Where(r => r.State != RowState.Stopped).Select(r => r.Id)];
        ImmutableArray<string> all = [.. rows.Select(r => r.Id)];
        var stopIsMain = active.Length > 0;

        RowAction Engine(RowActionKind kind, string label, ContainerAction action, ImmutableArray<string> ids, bool main) =>
            new(kind, label, canAct, main ? ButtonWhen.Always : ButtonWhen.Wide, main, action, ids);

        RowAction[] run =
        [
            .. stopped.IsEmpty ? (RowAction[])[] : [Engine(RowActionKind.Start, "Start", ContainerAction.Start, stopped, main: !stopIsMain)],
            .. active.IsEmpty ? (RowAction[])[] : [Engine(RowActionKind.Stop, "Stop", ContainerAction.Stop, active, main: true)],
            Engine(RowActionKind.Restart, "Restart", ContainerAction.Restart, all, main: false),
        ];
        RowAction delete = new(RowActionKind.Delete, "Delete", canAct, ButtonWhen.Always, IsMain: false, EngineAction: null, all);

        return new RowActions([run, [delete]], BrowserUrl: null);
    }
}

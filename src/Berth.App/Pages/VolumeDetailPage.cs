using System.Collections.Immutable;
using Berth.Components;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>What the app gives a volume's page.</summary>
/// <param name="Route">The page's route: the volume it shows, and on which engine session.</param>
/// <param name="Leave">Returns to the volumes list; with a text, the list shows it in an info bar ("data was removed.").</param>
internal sealed record VolumeDetailProps(AppRoute.VolumeDetail Route, Action<string?> Leave);

/// <summary>
/// One volume: its header (the way back, name with copy, whether it is in use, driver, mount point, size, created, its actions) and the
/// "Containers in use" tab, the containers that mount it with where and how, each a link to its page. Everything comes from the session's
/// lists (volumes, containers, disk use); nothing is read for the page alone. A volume that is no longer listed sends the page back to
/// the list.
/// </summary>
internal sealed class VolumeDetailPage : EngineStateComponent<VolumeDetailProps>
{
    private const string VolumesTag = "volumes";

    private const double RowHeight = 44;

    private enum UserColumn { Container, Destination, Access }

    private static readonly SelectorBarItemData[] Tabs = [SelectorBarItem("Containers in use")];

    private static readonly ListColumns<UserColumn> Columns = new(
        ListColumn<UserColumn>.Of(GridSize.Star(2)) with { Title = "Container" },
        ListColumn<UserColumn>.Of(GridSize.Star(3)) with { Title = "Mounted at" },
        ListColumn<UserColumn>.Of(GridSize.Px(140)) with { Title = "Access" });

    private static readonly ListSortView<UserColumn> NoSort = new(UserColumn.Container, false, _ => "", _ => { });

    private static readonly GridSize[] HeaderColumns = [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto];
    private static readonly GridSize[] TitleColumns = [GridSize.Star(), GridSize.Auto];
    private static readonly GridSize[] FactColumns = [GridSize.Auto, GridSize.Star()];
    private static readonly GridSize[] OneRow = [GridSize.Star()];
    private static readonly GridSize[] FourRows = [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto];

    // The breadcrumb items last made, for the volume named; read and written on the UI thread only.
    private static (string? Name, BreadcrumbBarItemData[] Items) _crumbs = (null, []);

    private static readonly object NoBatch = new();

    public override Element Render()
    {
        var name = Props.Route.Name;
        var leave = Props.Leave;
        var state = UseEngineState();
        var nav = UseNavigation<AppRoute>();
        var (confirm, setConfirm) = UseState<RemovalPlan?>(null);

        var active = nav.CurrentRoute == Props.Route;
        var connected = state.Status == ConnectionStatus.Connected;
        var row = UseMemo(() => VolumeRows.From(state).FirstOrDefault(r => r.Name == name), state.Volumes, state.Containers, state.DiskUsage.Usage);
        var users = UseMemo(() => VolumeRows.Users(state, name), state.Containers);

        // Removed (here, in the list, or from outside Berth): the list read since no longer has it, and no remove of it is still under way.
        var gone = state.Volumes.Loaded && row is null && !state.Volumes.Pending.ContainsKey(name);
        UseEffect(
            () =>
            {
                if (active && gone)
                    leave($"{name} was removed.");
            },
            active,
            gone);

        void AskDelete()
        {
            if (row is null)
                return;
            var plan = Confirmations.RemoveVolumes((VolumeRow[])[row], (string[])[name], connected);
            if (!plan.IsEmpty)
                setConfirm(plan);
        }

        void OpenContainer(string id) =>
            nav.Navigate(new AppRoute.ContainerDetail(id, AppServices.Current.SessionNumber), new NavigateOptions { Transition = NavigationTransition.DrillIn() });

        var keys = confirm is not null
            ? new PageKeys()
            : new PageKeys(
                Reload: connected
                    ? () =>
                    {
                        AppServices.Current.Session.Dispatch(new ReloadRequested());
                        AppServices.Current.Session.Dispatch(new DiskUsageRequested());
                    }
                    : null,
                Delete: row is not null && RowSelection.CanAct(row, connected) ? AskDelete : null,
                Escape: () => leave(null),
                Back: () => leave(null));
        var route = Props.Route;
        UseEffect(() => ShellKeys.Register(route, keys), keys);

        var removal = state.Volumes.Removal;
        var removalPanel = UseMemo(() => RemovalPanel.Render(removal, ResourceKind.Volumes), removal ?? NoBatch);
        var header = UseMemo(
            () => row is null ? null : Header(row, connected, () => leave(null), AskDelete).WithKey("header"),
            row,
            connected);

        var page = (FlexColumn(
                header,
                ConnectionBar.Render(state, () => AppServices.Current.Session.Dispatch(new ConnectRequested()))?.WithKey("connection"),
                removalPanel?.WithKey("removal"),
                SelectorBar(Tabs, 0).AutomationId("volume-tabs").Flex(shrink: 0).WithKey("tabs"),
                Content(row, users, OpenContainer).Flex(grow: 1, basis: 0).WithKey("content"),
                (confirm is null
                    ? Empty()
                    : RemoveDialog.Render(confirm.Title, confirm.Text, canConfirm: true, () => ConfirmDelete(name), () => setConfirm(null))).WithKey("dialog"))
            with
            {
                RowGap = 12,
            })
            .FlexPadding(24, 16, 24, 0);

        return page.Flex(grow: 1, basis: 0);
    }

    /// <summary>Sends the confirmed delete as it stands now, read from the current session; never forced.</summary>
    private static void ConfirmDelete(string name)
    {
        var state = AppServices.Current.Session.State;
        RemoveDialog.Send(Confirmations.RemoveVolumes(VolumeRows.From(state), (string[])[name], state.Status == ConnectionStatus.Connected));
    }

    /// <summary>
    /// "Volumes / name" and a back button; the volume's name with copy and whether a container uses it; driver, mount point, size and
    /// created; its actions (Copy name in the "more" menu, Delete). The right-click menu has the same actions.
    /// </summary>
    private static Element Header(VolumeRow row, bool connected, Action onBack, Action onDelete)
    {
        var actions = RowActions.ForVolume(row, connected, viewDetails: false);
        void Run(RowAction action)
        {
            if (action.Kind == RowActionKind.CopyName)
                ContainerParts.CopyToClipboard(row.Name);
            else if (action.Kind == RowActionKind.Delete)
                onDelete();
        }

        var breadcrumb = BreadcrumbBar(
                Crumbs(row.Name),
                item =>
                {
                    if (Equals(item.Tag, VolumesTag))
                        onBack();
                })
            .AutomationName("Location");

        var back = Widgets.IconButton(Glyphs.ChevronLeft, "Back to volumes (Esc)", onBack)
            .AutomationId("back-to-list")
            .VAlign(VerticalAlignment.Center);

        var icon = Widgets.Glyph(Glyphs.HardDrive, size: 24, color: Theme.SecondaryText).VAlign(VerticalAlignment.Center).Margin(4, 0, 12, 0);

        var usage = row.IsPending
            ? (Element)HStack(8, ProgressRing().Size(12, 12).AutomationName("Working"), Caption("Working…").Foreground(Theme.SecondaryText))
            : row.InUse
                ? HStack(6, Widgets.StatusDot(Theme.SystemSuccess).VAlign(VerticalAlignment.Center), Caption("In use").Foreground(Theme.SecondaryText))
                : Caption("Not in use").Foreground(Theme.SecondaryText);

        var title = Grid(
            TitleColumns,
            OneRow,
            [
                Widgets.OneLine(Widgets.SectionTitle(row.Name)).ToolTip(row.Name).AutomationId("volume-name").VAlign(VerticalAlignment.Center).Grid(column: 0),
                Widgets.IconButton(Glyphs.Copy, "Copy name", () => ContainerParts.CopyToClipboard(row.Name), size: 12)
                    .Padding(4)
                    .MinWidth(24)
                    .MinHeight(24)
                    .Margin(4, 0, 0, 0)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(column: 1),
            ]).HAlign(HorizontalAlignment.Left);

        var identity = (FlexColumn(title, usage.AutomationId("volume-in-use")) with { RowGap = 2 })
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 24, 0);

        var summary = Border(
                Grid(
                    HeaderColumns,
                    OneRow,
                    [
                        back.Grid(column: 0),
                        icon.Grid(column: 1),
                        identity.Grid(column: 2),
                        ContainerParts.ActionButtons(actions, compact: false, row.Name, Run).Grid(column: 3),
                    ])
                .Padding(0, 0, 0, 12))
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 0, 0, 1);

        var now = DateTimeOffset.UtcNow;
        var facts = Grid(
                FactColumns,
                FourRows,
                [
                    .. Fact(0, "Driver", row.Driver, "volume-driver"),
                    .. Fact(1, "Mount point", row.Mountpoint, "volume-mountpoint"),
                    .. Fact(2, "Size", ByteFormat.Bytes(row.Size), "volume-size"),
                    .. Fact(3, "Created", AgeFormat.Ago(now, row.Created), "volume-created"),
                ])
            .Padding(0, 0, 0, 12);

        return (FlexColumn(breadcrumb, summary, Border(facts).BorderBrush(Theme.DividerStroke).BorderThickness(0, 0, 0, 1)) with { RowGap = 8 })
            .WithContextFlyout(ContainerParts.ContextMenu(actions, Run))
            .Flex(shrink: 0);
    }

    /// <summary>A label and its value on row <paramref name="index"/> of the facts grid; the value is selectable, trimmed with a tooltip.</summary>
    private static Element[] Fact(int index, string label, string value, string automationId) =>
    [
        Caption(label).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Margin(0, 2, 24, 2).Grid(row: index, column: 0),
        Widgets.OneLine(Body(value)).IsTextSelectionEnabled().ToolTip(value).AutomationId(automationId).VAlign(VerticalAlignment.Center).Margin(0, 2, 0, 2).Grid(row: index, column: 1),
    ];

    /// <summary>"Volumes > name", the same array while the name stays (a new array each render is a new item list for the control).</summary>
    private static BreadcrumbBarItemData[] Crumbs(string name)
    {
        var last = _crumbs;
        if (last.Name == name)
            return last.Items;

        BreadcrumbBarItemData[] items = [Breadcrumb("Volumes", VolumesTag), Breadcrumb(name)];
        _crumbs = (name, items);
        return items;
    }

    /// <summary>The containers that mount the volume, or why there are none; a ring until the volume is listed.</summary>
    private static Element Content(VolumeRow? row, IReadOnlyList<VolumeUser> users, Action<string> openContainer)
    {
        if (row is null)
            return ProgressRing().Size(32, 32).AutomationName("Loading the volume").HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Top).Margin(0, 24, 0, 0);
        if (users.Count == 0)
            return ListTable.EmptyState("No container uses this volume.").AutomationId("volume-users-empty");

        var header = ListTable.Header(Columns, compact: false, checkedState: false, connected: true, _ => { }, NoSort);
        return ListTable.Table(header, users, u => u.ContainerId + "|" + u.Destination, u => ListTable.Row(
                Columns,
                compact: false,
                RowHeight,
                HyperlinkButton(Widgets.OneLine(Body(u.ContainerName)), onClick: () => openContainer(u.ContainerId))
                    .AutomationId("open-" + u.ContainerName)
                    .AutomationName(u.ContainerName)
                    .ToolTip(u.ContainerName)
                    .Padding(0)
                    .HAlign(HorizontalAlignment.Left)
                    .VAlign(VerticalAlignment.Center)
                    .Margin(0, 0, 12, 0)
                    .Grid(column: 0),
                ListTable.CellText(u.Destination).Grid(column: 1),
                ListTable.CellText(u.ReadWrite ? "Read and write" : "Read only").Grid(column: 2)))
            .AutomationId("volume-users");
    }
}

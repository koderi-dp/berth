using Berth.Engine.Models;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>
/// The top of a container's detail page: the way back ("Containers / name" and a back button), the container's state, name, short
/// ID with copy, image and published ports, its status and since when, and its actions. Everything but the time a running
/// container started comes from the list's row, which follows the engine's events, so the header changes as the list does.
/// </summary>
internal static class DetailHeader
{
    private const string ContainersTag = "containers";

    /// <summary>How many ports the header lists before it sums up the rest.</summary>
    private const int ShownPorts = 4;

    /// <summary>The height of the name, the ID line and the ports line.</summary>
    private const double SummaryHeight = 88;

    private static readonly GridSize[] Columns = [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto];
    private static readonly GridSize[] IdColumns = [GridSize.Auto, GridSize.Auto, GridSize.Star()];
    private static readonly GridSize[] OneRow = [GridSize.Star()];

    /// <param name="inspected">The state from the last read of the container, for since when it runs; null before the first.</param>
    /// <param name="compact">A narrow window: Restart and Pause go into the "more" menu, as on a list row.</param>
    /// <param name="onBack">Returns to the list.</param>
    /// <param name="run">Carries out an engine action or Delete (which the page confirms first).</param>
    public static Element Render(ContainerRow row, ContainerStateDetails? inspected, bool connected, bool compact, Action onBack, Action<RowAction> run)
    {
        var actions = RowActions.ForContainer(row, connected, viewDetails: false);
        void Run(RowAction action) => ContainerParts.Run(action, row, actions, run);

        var breadcrumb = BreadcrumbBar(
                [Breadcrumb("Containers", ContainersTag), Breadcrumb(row.Name)],
                item =>
                {
                    if (Equals(item.Tag, ContainersTag))
                        onBack();
                })
            .AutomationName("Location");

        var back = Widgets.IconButton(Glyphs.ChevronLeft, "Back to containers (Esc)", onBack)
            .AutomationId("back-to-list")
            .VAlign(VerticalAlignment.Center);

        var mark = ContainerParts.StateMark(ContainerStatus.Shape(row.State), ContainerParts.StateColor(row.State), row.State.ToString(), row.Pending is not null)
            .Margin(4, 0, 12, 0);

        var identity = (FlexColumn(
                OneLine(Widgets.SectionTitle(row.Name)).ToolTip(row.Name),
                IdAndImage(row),
                Ports(row.Ports))
            with
            {
                RowGap = 2,
            })
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 24, 0);

        var headline = ContainerStatus.Headline(row.State, row.Status, inspected, DateTimeOffset.UtcNow);
        var status = (FlexColumn(
                Caption("STATUS").Foreground(Theme.SecondaryText),
                Body(headline).Foreground(ContainerStatus.IsFailedExit(row.Status) ? Theme.SystemCritical : Theme.PrimaryText)
                    .AutomationId("detail-status"))
            with
            {
                RowGap = 2,
            })
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 16, 0);

        // As tall with ports as without, so the tabs do not move when a stopped container's ports go; a line under it, as above the list.
        var summary = Border(
                Grid(
                    Columns,
                    OneRow,
                    [
                        back.Grid(column: 0),
                        mark.Grid(column: 1),
                        identity.Grid(column: 2),
                        status.Grid(column: 3),
                        ContainerParts.ActionButtons(actions, compact, row.Name, Run).Grid(column: 4),
                    ])
                .MinHeight(SummaryHeight)
                .Padding(0, 0, 0, 12))
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 0, 0, 1);

        return (FlexColumn(breadcrumb, summary) with { RowGap = 8 })
            .WithContextFlyout(ContainerParts.ContextMenu(actions, Run))
            .Flex(shrink: 0);
    }

    /// <summary>The short ID with a copy button, then the image, which ends in an ellipsis when it does not fit.</summary>
    private static Element IdAndImage(ContainerRow row) =>
        Grid(
            IdColumns,
            OneRow,
            [
                Caption(row.ShortId).Foreground(Theme.SecondaryText).ToolTip(row.Id).VAlign(VerticalAlignment.Center).Grid(column: 0),
                Widgets.IconButton(Glyphs.Copy, "Copy ID", () => ContainerParts.CopyToClipboard(row.Id), size: 12)
                    .Padding(4)
                    .MinWidth(24)
                    .MinHeight(24)
                    .Margin(2, 0, 12, 0)
                    .Grid(column: 1),
                OneLine(Caption(row.Image)).Foreground(Theme.SecondaryText).ToolTip(row.Image).VAlign(VerticalAlignment.Center).Grid(column: 2),
            ]);

    /// <summary>The published ports as links, then "+n" for the rest (listed in its tooltip); nothing when there is no port.</summary>
    private static Element? Ports(IReadOnlyList<PortLink> ports)
    {
        if (ports.Count == 0)
            return null;

        var rest = ports.Count > ShownPorts
            ? Caption("+" + (ports.Count - ShownPorts)).Foreground(Theme.SecondaryText).ToolTip(string.Join(", ", ports.Skip(ShownPorts).Select(p => p.Text)))
            : null;

        return (FlexRow([.. ports.Take(ShownPorts).Select(ContainerParts.PortLink), rest]) with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 4,
            })
            .Margin(-4, 0, 0, 0); // the link's own padding: its text lines up with the ID above
    }

    private static TextBlockElement OneLine(TextBlockElement text) =>
        text.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis);
}

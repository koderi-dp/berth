using Berth.Components;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Detail;

/// <summary>What the Stats tab shows.</summary>
/// <param name="ContainerId">The container, for Retry.</param>
/// <param name="Stats">The samples so far; a new instance each second while they come, so the tab draws again only then.</param>
/// <param name="Error">Why the stats stream failed, as the engine said, when <paramref name="Status"/> is Failed.</param>
/// <param name="Waiting">
/// The page waits for the engine (see <see cref="DetailPage.Waiting"/>): the charts stay, or the tab says it waits; no Retry.
/// </param>
/// <param name="CpuLimit">The container's CPU limit in CPUs, or null.</param>
/// <param name="MachineMemory">The engine's memory in bytes (0 before its info is read): a memory limit below it is drawn.</param>
internal sealed record StatsTabProps(
    string ContainerId,
    StatsHistory Stats,
    StreamStatus Status,
    string? Error,
    bool Waiting,
    double? CpuLimit,
    long MachineMemory);

/// <summary>
/// The "Stats" tab of a container's detail page: CPU, memory, network and disk over the last two minutes, four charts in a two by
/// two grid, one column when the tab is narrow. While the container is not running, its stats are loading or have failed, a
/// sentence (or the engine's error with Retry) takes their place.
/// </summary>
internal sealed class StatsTab : Component<StatsTabProps>
{
    /// <summary>The tab is at least this wide (in effective pixels) when the charts sit two by two.</summary>
    private const double TwoColumnsMinWidth = 760;

    /// <summary>A chart card's height when the charts stand in one column.</summary>
    private const double OneColumnCardHeight = 280;

    /// <summary>A chart card is never shorter than this; when the tab is lower the charts scroll.</summary>
    private const double MinCardHeight = 220;

    private const double Gap = 12;

    public override Element Render()
    {
        var props = Props;
        var dark = UseIsDarkTheme();
        var (width, setWidth) = UseState(0.0);
        var (height, setHeight) = UseState(0.0);

        var view = StatsCharts.View(props.Status, props.Stats.Latest is not null, props.Waiting);
        var charts = UseMemo(
            () => view == StatsView.Charts ? Charts(props, width, height, dark) : null,
            props.Stats,
            view,
            width,
            height,
            dark,
            props.CpuLimit ?? -1,
            props.MachineMemory);

        Element content = view switch
        {
            StatsView.Loading => Widgets.CenteredNote(StatsCharts.LoadingText).AutomationId("stats-loading"),
            StatsView.NotRunning => Widgets.CenteredNote(StatsCharts.NotRunningText).AutomationId("stats-not-running"),
            StatsView.Failed => FailureBar(props),
            StatsView.Waiting => Widgets.CenteredNote(StatsCharts.WaitingText).AutomationId("stats-waiting"),
            _ => charts ?? Empty(),
        };

        // The charts are drawn at the size the tab has: it is measured here (rounded down to 8 px, so a resize by a pixel or two
        // does not draw them again).
        return ScrollViewer(content)
            .VerticalScrollMode(ScrollMode.Auto)
            .HorizontalScrollMode(ScrollMode.Disabled)
            .OnSizeChanged((_, e) =>
            {
                setWidth(Math.Floor(e.NewSize.Width / 8) * 8);
                setHeight(Math.Floor(e.NewSize.Height / 8) * 8);
            })
            .Margin(0, 0, 0, 16)
            .AutomationId("stats-tab");
    }

    /// <summary>The engine's message, as it gave it, when the stats could not be read; Retry opens the page's session again.</summary>
    private static Element FailureBar(StatsTabProps props) =>
        (InfoBar(message: props.Error ?? "") with
        {
            IsOpen = true,
            ActionButtonContent = "Retry",
            OnActionButtonClick = () => AppServices.Current.OpenDetail(props.ContainerId),
        })
            .Error()
            .IsClosable(false)
            .VAlign(VerticalAlignment.Top)
            .AutomationId("stats-error");

    /// <summary>The four chart cards laid out for a tab of <paramref name="width"/> by <paramref name="height"/>.</summary>
    private static Element? Charts(StatsTabProps props, double width, double height, bool dark)
    {
        if (width <= 0 || props.Stats.Latest is not { } latest)
            return null;

        var history = props.Stats;
        var memoryLimit = StatsCharts.MemoryLimit(latest, props.MachineMemory);
        StatsChartData[] charts =
        [
            new("CPU usage", StatsCharts.CpuSummary(history), [new(history.Cpu, null, StatsCharts.CpuValue(latest), Dashed: false)],
                StatsCharts.Cpu(history.Cpu, props.CpuLimit), latest.Time,
                props.CpuLimit is > 0 and var cpus ? new ChartReference(cpus * 100, "Limit " + StatsSummary.Percent(cpus * 100)) : null),
            new("Memory usage", StatsCharts.MemorySummary(history), [new(history.Memory, null, StatsCharts.MemoryValue(latest), Dashed: false)],
                StatsCharts.Memory(history.Memory, memoryLimit), latest.Time,
                memoryLimit is { } limit ? new ChartReference(limit, "Limit " + ByteFormat.Bytes(limit)) : null),
            new("Disk read/write", StatsCharts.DiskSummary(history),
                [
                    new(history.BlockRead, "Read", ByteFormat.Rate(Last(history.BlockRead)), Dashed: false),
                    new(history.BlockWritten, "Written", ByteFormat.Rate(Last(history.BlockWritten)), Dashed: true),
                ],
                StatsCharts.Rate(history.BlockRead, history.BlockWritten), latest.Time),
            new("Network I/O", StatsCharts.NetworkSummary(history),
                [
                    new(history.NetworkReceived, "Received", ByteFormat.Rate(Last(history.NetworkReceived)), Dashed: false),
                    new(history.NetworkSent, "Sent", ByteFormat.Rate(Last(history.NetworkSent)), Dashed: true),
                ],
                StatsCharts.Rate(history.NetworkReceived, history.NetworkSent), latest.Time),
        ];
        string[] ids = ["stats-cpu", "stats-memory", "stats-disk", "stats-network"];

        var columns = width >= TwoColumnsMinWidth ? 2 : 1;
        var cardWidth = Math.Floor((width - (Gap * (columns - 1))) / columns);
        var cardHeight = columns == 2 ? Math.Max(MinCardHeight, Math.Floor((height - Gap) / 2)) : OneColumnCardHeight;
        // The plot gets what the card leaves after its padding, border and title row.
        var plotWidth = cardWidth - (2 * StatsChart.CardPadding) - 2;
        var plotHeight = cardHeight - (2 * StatsChart.CardPadding) - 2 - StatsChart.HeaderHeight - StatsChart.HeaderGap;

        var cells = charts.Select((chart, i) =>
            StatsChart.Render(chart, plotWidth, plotHeight, dark, ids[i])
                .Height(cardHeight)
                .Grid(row: i / columns, column: i % columns));

        var columnSizes = columns == 2 ? new[] { GridSize.Star(), GridSize.Star() } : [GridSize.Star()];
        var rowSizes = Enumerable.Repeat(GridSize.Auto, 4 / columns).ToArray();
        return (Grid(columnSizes, rowSizes, [.. cells]) with
            {
                ColumnSpacing = Gap,
                RowSpacing = Gap,
            })
            .Width(width);
    }

    private static double Last(IReadOnlyList<StatsPoint> points) => points.Count > 0 ? points[^1].Value : 0;
}

using Berth.Components;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Charting;
using Microsoft.UI.Reactor.Charting.Accessibility;
using Microsoft.UI.Reactor.Charting.D3;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Detail;

/// <summary>One series of a stats chart: its points, its name and figure for the legend, and whether it is drawn dashed.</summary>
/// <param name="Name">The legend's name ("Received"); null for a chart of one series, which its title names.</param>
internal sealed record ChartSeries(IReadOnlyList<StatsPoint> Points, string? Name, string Figure, bool Dashed);

/// <summary>A horizontal line across the plot at <paramref name="Value"/> (bytes or percent) with its label, such as a memory limit.</summary>
internal sealed record ChartReference(double Value, string Label);

/// <summary>What one stats chart card draws.</summary>
/// <param name="Title">The card's title ("CPU usage").</param>
/// <param name="Summary">What a screen reader hears for the card: the latest and highest figures.</param>
internal sealed record StatsChartData(
    string Title,
    string Summary,
    ChartSeries[] Series,
    ChartScale Scale,
    DateTimeOffset Latest,
    ChartReference? Reference = null);

/// <summary>
/// A stats chart card: the title with the latest figure (one series) or a legend with each series' figure (two series, the second
/// dashed so colour is not the only difference), and a line chart of the last two minutes with the y-axis from 0. Drawn with
/// Reactor's D3 primitives on a canvas of a fixed size; the built-in chart element cannot fix its axes' domains or draw two series.
/// </summary>
internal static class StatsChart
{
    /// <summary>Height of the title row above the plot.</summary>
    public const double HeaderHeight = 28;

    /// <summary>Space between the title row and the plot.</summary>
    public const double HeaderGap = 8;

    /// <summary>Padding inside the card.</summary>
    public const double CardPadding = 16;

    // Room for the y labels ("1000 KB/s") on the left and the time labels below.
    private const double MarginLeft = 64;
    private const double MarginRight = 16;
    private const double MarginTop = 8;
    private const double MarginBottom = 24;

    private static readonly double[] Dash = [4, 2];

    /// <summary>The radius of the dot that stands for a series of one point.</summary>
    private const double DotRadius = 3;

    // The series colours: a blue and an orange checked for colour-blind separation and 3:1 contrast on the light and the dark
    // surface (the dataviz palette validator). There are no chart colours among the theme tokens.
    private static readonly ChartPalette LightPalette = ChartPalette.FromColors(D3Color.Parse("#2a78d6"), D3Color.Parse("#eb6834"));
    private static readonly ChartPalette DarkPalette = ChartPalette.FromColors(D3Color.Parse("#3987e5"), D3Color.Parse("#d95926"));

    private static readonly Dictionary<(bool Dark, int Series, bool Fill), Brush> Brushes = [];

    /// <summary>The card for <paramref name="data"/> with a plot of <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static Element Render(StatsChartData data, double width, double height, bool dark, string automationId)
    {
        var header = Header(data, dark);
        var plot = Plot(data, Math.Max(width, MarginLeft + MarginRight + 40), Math.Max(height, MarginTop + MarginBottom + 40), dark);

        return Border(
                (FlexColumn(header, plot) with { RowGap = HeaderGap }))
            .Background(Theme.CardBackground)
            .BorderBrush(Theme.CardStroke)
            .BorderThickness(1)
            .CornerRadius(8)
            .Padding(CardPadding)
            .AutomationId(automationId);
    }

    /// <summary>
    /// The title and the latest figures. The title carries the summary as its accessible name (a heading, so a screen reader can
    /// jump between the charts); the figures beside it are part of that summary and hidden from the reader.
    /// </summary>
    private static Element Header(StatsChartData data, bool dark)
    {
        var title = Widgets.StrongText(data.Title)
            .AutomationName(data.Summary)
            .HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);

        var figures = data.Series.Length == 1
            ? [Body(data.Series[0].Figure).Foreground(Theme.SecondaryText).AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)]
            : data.Series.Select((s, i) => LegendItem(s, i, dark)).ToArray();

        return (FlexRow([title, .. figures]) with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 16,
            })
            .Height(HeaderHeight)
            .Flex(shrink: 0);
    }

    /// <summary>A legend entry: a short line in the series' colour and dash, its name and its latest figure.</summary>
    private static Element LegendItem(ChartSeries series, int index, bool dark)
    {
        var swatch = D3Charts.D3Canvas(16, 12, D3Path("M0,6 L16,6", series, index, dark));
        return HStack(
                6,
                swatch.VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center),
                Caption($"{series.Name} {series.Figure}").Foreground(Theme.SecondaryText).VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center))
            .AccessibilityView(AccessibilityView.Raw);
    }

    /// <summary>The plot: grid, the reference line, the series and the axes. Decorative for UI Automation; the header says it all.</summary>
    private static Element Plot(StatsChartData data, double width, double height, bool dark)
    {
        // The D3 axis and grid brushes follow this flag; it is per thread, and the UI thread draws every chart.
        D3Charts.IsDarkTheme = dark;

        var plotWidth = width - MarginLeft - MarginRight;
        var plotHeight = height - MarginTop - MarginBottom;
        var xs = new LinearScale([-StatsCharts.WindowSeconds, 0], [MarginLeft, MarginLeft + plotWidth]);
        var ys = new LinearScale([0, data.Scale.Max], [MarginTop + plotHeight, MarginTop]);
        var scale = data.Scale;
        var latest = data.Latest;

        double X(StatsPoint p) => xs.Map(-StatsCharts.SecondsAgo(latest, p.Time));
        double Y(double value) => ys.Map(Math.Min(scale.ToUnits(value), scale.Max));

        var children = new List<Element>();
        children.AddRange(D3Charts.D3Grid(ys, MarginLeft, plotWidth, StatsCharts.TickCount));

        if (data.Series.Length == 1 && data.Series[0].Points.Count > 1)
        {
            var baseline = MarginTop + plotHeight;
            children.Add(D3Charts.D3AreaPath(data.Series[0].Points, X, _ => baseline, p => Y(p.Value), fill: SeriesBrush(dark, 0, fill: true)));
        }

        if (data.Reference is { } reference)
        {
            var y = Y(reference.Value);
            var path = FormattableString.Invariant($"M{MarginLeft},{y} L{MarginLeft + plotWidth},{y}");
            children.Add(D3Charts.D3Path(path, D3Charts.ChartMutedForeground, strokeWidth: 1).StrokeDashArray(Dash));
            // Its label sits on the line's right end, above it, in the theme's secondary text colour.
            children.Add(TickText(reference.Label).Canvas(MarginLeft + plotWidth - 4, y - 2, 1, 1));
        }

        for (var i = 0; i < data.Series.Length; i++)
        {
            var series = data.Series[i];
            if (series.Points.Count == 0)
                continue;
            if (series.Points.Count == 1)
            {
                // The first second: one point, which a line cannot draw; a dot shows it.
                var only = series.Points[0];
                children.Add(D3Charts.D3Circle(X(only), Y(only.Value), DotRadius).Fill(SeriesBrush(dark, i, fill: false)));
                continue;
            }

            var line = D3Charts.D3LinePath(series.Points, X, p => Y(p.Value), SeriesBrush(dark, i, fill: false), 2)
                .StrokeLineJoin(PenLineJoin.Round);
            children.Add(series.Dashed ? line.StrokeDashArray(Dash) : line);
        }

        children.AddRange(D3Charts.D3Axes(
            xs,
            ys,
            MarginLeft,
            MarginTop,
            plotWidth,
            plotHeight,
            xTicks: plotWidth >= 360 ? 6 : 3,
            yTicks: StatsCharts.TickCount,
            xTickLabel: t => TickText(StatsCharts.TimeTick(-t)),
            yTickLabel: t => TickText(scale.TickLabel(t))));

        return D3Charts.D3Canvas(width, height, [.. children])
            .AccessibilityView(AccessibilityView.Raw)
            .Flex(shrink: 0);
    }

    private static Element TickText(string text) => Caption(text).FontSize(11).Foreground(Theme.SecondaryText);

    private static Element D3Path(string path, ChartSeries series, int index, bool dark)
    {
        var line = D3Charts.D3Path(path, SeriesBrush(dark, index, fill: false), strokeWidth: 2);
        return series.Dashed ? line.StrokeDashArray(Dash) : line;
    }

    /// <summary>A series' line brush, or its area fill (the same colour, faint); one instance per theme, series and kind.</summary>
    private static Brush SeriesBrush(bool dark, int series, bool fill)
    {
        if (Brushes.TryGetValue((dark, series, fill), out var brush))
            return brush;

        var color = (dark ? DarkPalette : LightPalette)[series];
        brush = fill ? D3Charts.Brush(color, 0.16) : D3Charts.Brush(color);
        Brushes[(dark, series, fill)] = brush;
        return brush;
    }
}

using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>The figures the strip shows (from <c>StatsSummary.CpuText</c> and <c>MemoryText</c>); equal figures draw nothing again.</summary>
internal sealed record StatsStripProps(string Cpu, string Memory);

/// <summary>
/// The line above the containers list with the running containers' CPU and memory in total, out of the engine's CPUs and memory.
/// Each figure is read by a screen reader together with its label.
/// </summary>
internal sealed class StatsStrip : Component<StatsStripProps>
{
    public override Element Render() =>
        (FlexRow(
                Figure("Container CPU usage", Props.Cpu, "stats-total-cpu"),
                Figure("Container memory usage", Props.Memory, "stats-total-memory"))
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 32,
            })
            .Flex(shrink: 0);

    private static Element Figure(string label, string value, string automationId) =>
        HStack(
            8,
            Caption(label).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).AccessibilityView(AccessibilityView.Raw),
            Widgets.StrongText(value).VAlign(VerticalAlignment.Center).AutomationName($"{label} {value}").AutomationId(automationId));
}

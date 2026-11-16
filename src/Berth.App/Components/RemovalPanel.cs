using Berth.Presentation;
using Berth.Session;
using Berth.State;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>
/// A list's batch of removes above its table (images, volumes, networks): while it runs, how far it is and a progress bar; once it
/// has ended, what was removed, each refusal as the engine gave it and each skip with why, in a panel the user closes.
/// </summary>
internal static class RemovalPanel
{
    /// <summary>How tall the list of refusals and skips grows before it scrolls.</summary>
    private const double DetailsMaxHeight = 160;

    /// <summary>
    /// The panel for <paramref name="batch"/>, or null when there is none; <paramref name="nameOf"/> names an item by its key (by default
    /// the key). Build it once per batch (a <c>UseMemo</c>).
    /// </summary>
    public static Element? Render(RemovalBatch? batch, ResourceKind kind, Func<string, string>? nameOf = null)
    {
        if (batch is null)
            return null;

        var summary = RemovalProgress.Summary(batch);
        if (batch.IsRunning)
        {
            return (FlexRow(
                    Body(summary).Foreground(Theme.PrimaryText).AutomationId("removal-progress").LiveRegion(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite),
                    Progress(batch.Total == 0 ? 0 : 100.0 * batch.Finished / batch.Total).Width(200).VAlign(VerticalAlignment.Center))
                with
                {
                    AlignItems = FlexAlign.Center,
                    ColumnGap = 16,
                })
                .Flex(shrink: 0);
        }

        var details = RemovalProgress.Details(batch, nameOf ?? (key => key));
        var problems = batch.Interrupted || details.Count > 0;
        var bar = (InfoBar(title: summary) with { IsOpen = true })
            .IsClosable()
            .Closed(() => AppServices.Current.Session.Dispatch(new RemovalDismissed(kind)))
            .AutomationId("removal-result");
        if (details.Count > 0)
        {
            // A stack, not a flex column: each line wraps in the panel's width (an engine's refusal can be long).
            var lines = DialogParts.Stack(4, [.. details.Select(line => Caption(line).TextWrapping().IsTextSelectionEnabled().Foreground(Theme.PrimaryText))]);
            bar = bar.Content(ScrollViewer(lines).MaxHeight(DetailsMaxHeight).Margin(0, 0, 0, 12));
        }

        return (problems ? bar.Warning() : bar.Success()).Flex(shrink: 0);
    }
}

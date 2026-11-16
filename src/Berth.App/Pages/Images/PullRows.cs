using Berth.Components;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Images;

/// <summary>
/// Which pull row takes the focus: the one of <paramref name="FocusKey"/>, each time <paramref name="FocusRequest"/> goes up (the user
/// asked to pull a reference that is already being pulled).
/// </summary>
internal sealed record PullRowsProps(string? FocusKey, int FocusRequest)
{
    /// <summary>Called with a pull's reference after its Cancel was clicked; one instance kept by the page, so the props stay equal.</summary>
    public Action<string>? OnCancelled { get; init; }
}

/// <summary>
/// The pulls the user started, at the top of the images table, in the order they started. It follows the engine state itself, so a
/// pull's progress (up to ten changes a second) renders only these rows, and each row only when its own pull changed.
/// </summary>
internal sealed class PullRows : EngineStateComponent<PullRowsProps>
{
    public override Element Render()
    {
        var state = UseEngineState();
        var pulls = state.Pulls;
        var ordered = UseMemo(() => pulls.Values.OrderBy(p => p.Generation).ToArray(), pulls);
        if (ordered.Length == 0)
            return Empty();

        return FlexColumn(
            [
                .. ordered.Select(pull => Component<PullRow, PullRowProps>(
                        new(pull, pull.Reference == Props.FocusKey ? Props.FocusRequest : 0, Props.OnCancelled))
                    .WithKey(pull.Reference)),
            ]) with
        {
            RowGap = 0,
        };
    }
}

/// <summary>One pull and when it should take the focus (a count that goes up, 0 for never).</summary>
internal sealed record PullRowProps(ImagePull Pull, int FocusRequest, Action<string>? OnCancelled);

/// <summary>
/// A pull as a row: the reference, its progress line and bar, and Cancel; once failed or stopped, the engine's message (or that the
/// connection changed), Try again and Dismiss.
/// </summary>
internal sealed class PullRow : Component<PullRowProps>
{
    private static readonly GridSize[] Columns = [GridSize.Px(40), GridSize.Px(28), GridSize.Star(3), GridSize.Star(4), GridSize.Px(156)];
    private static readonly GridSize[] OneRow = [GridSize.Star()];

    public override Element Render()
    {
        var pull = Props.Pull;
        var reference = pull.Reference;
        var figure = PullFigure.Of(pull);
        var running = pull.Status == PullStatus.Running;
        var ended = pull.Status is PullStatus.Failed or PullStatus.Interrupted;
        var focusTarget = this.UseElementRef<Button>();

        UseEffect(
            () =>
            {
                if (Props.FocusRequest > 0 && focusTarget.Current is { } button)
                {
                    button.Focus(FocusState.Keyboard);
                    button.StartBringIntoView();
                }
            },
            Props.FocusRequest);

        var line = Caption(figure.Text)
            .TextWrapping(ended ? TextWrapping.Wrap : TextWrapping.NoWrap)
            .TextTrimming(ended ? TextTrimming.None : TextTrimming.CharacterEllipsis)
            .IsTextSelectionEnabled(ended)
            .Foreground(ended ? Theme.SystemCritical : Theme.SecondaryText)
            .ToolTip(figure.Text)
            .AutomationId("pull-status-" + reference)
            .LiveRegion(ended ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Off);
        Element? bar = running
            ? (figure.Fraction is { } fraction ? Progress(100 * fraction) : ProgressIndeterminate()).AutomationName("Pulling " + reference)
            : null;
        // A stack, not a flex column: the line wraps in the column's width (a failure is the engine's message, which can be long).
        var progress = DialogParts.Stack(4, line, bar)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 8, 12, 8);

        Element? buttons = running
            ? Button("Cancel", () =>
                {
                    Send(new PullCancelRequested(reference));
                    Props.OnCancelled?.Invoke(reference);
                })
                .AutomationId("cancel-pull-" + reference)
                .AutomationName("Cancel the pull of " + reference)
                .Ref(focusTarget)
            : ended
                ? (FlexRow(
                        Button("Try again", () => Send(new PullRequested(reference)))
                            .AutomationId("retry-pull-" + reference)
                            .AutomationName("Pull " + reference + " again")
                            .Ref(focusTarget),
                        Widgets.IconButton(Glyphs.Close, "Dismiss", () => Send(new PullDismissed(reference)))
                            .AutomationId("dismiss-pull-" + reference)
                            .AutomationName("Dismiss the pull of " + reference))
                    with
                    {
                        AlignItems = FlexAlign.Center,
                        JustifyContent = FlexJustify.FlexEnd,
                        ColumnGap = 4,
                    })
                : null;

        return Border(
                Grid(
                    Columns,
                    OneRow,
                    Widgets.Glyph(Glyphs.Download, size: 16, color: running ? Theme.AccentText : Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Left)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(column: 1),
                    Widgets.OneLine(Body(reference)).Foreground(Theme.PrimaryText).ToolTip(reference).VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0).Grid(column: 2),
                    progress.Grid(column: 3),
                    buttons?.HAlign(HorizontalAlignment.Right).VAlign(VerticalAlignment.Center).Grid(column: 4)).MinHeight(48).Padding(12, 0, 8, 0))
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 0, 0, 1)
            .AutomationId("pull-" + reference);
    }

    private static void Send(SessionEvent e) => AppServices.Current.Session.Dispatch(e);
}

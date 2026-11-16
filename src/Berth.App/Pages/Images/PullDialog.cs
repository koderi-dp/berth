using Berth.Components;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Images;

/// <param name="FocusPull">Called with a reference's key when the user asks to pull one already being pulled: the page shows its row.</param>
/// <param name="OnDone">Called once the dialog has closed.</param>
internal sealed record PullDialogProps(Action<string> FocusPull, Action OnDone);

/// <summary>
/// "Pull image": one field for the reference; Pull starts it and the dialog shows its progress line and bar. It can be closed while the
/// pull runs (the pull goes on, as a row at the top of the list); it closes itself when the pull is done; a failure shows the engine's
/// message, and the primary button tries again. Mounted for one use: it opens when mounted.
/// </summary>
internal sealed class PullDialog : EngineStateComponent<PullDialogProps>
{
    /// <summary>What the field reads when nothing is typed.</summary>
    private const string Example = "nginx:latest";

    public override Element Render()
    {
        var state = UseEngineState();
        var (text, setText) = UseState("");
        var (started, setStarted) = UseState<string?>(null);
        var (open, setOpen) = UseState(true);
        // The started pull has been seen in the state: from then on, its entry going away means it is done.
        var seen = UseRef(false);

        var connected = state.Status == ConnectionStatus.Connected;
        var key = ImagePull.KeyOf(text);
        var pull = started is not null && state.Pulls.TryGetValue(started, out var found) ? found : null;
        if (pull is not null)
            seen.Current = true;
        var running = pull?.Status == PullStatus.Running;
        var ended = pull?.Status is PullStatus.Failed or PullStatus.Interrupted;

        // Done (or already gone because the list shows the image): the list has it, so the dialog's work is over.
        var done = seen.Current && (pull is null || pull.Status == PullStatus.Done);
        UseEffect(
            () =>
            {
                if (done)
                    setOpen(false);
            },
            done);

        void Pull()
        {
            if (key is null || !connected)
                return;
            if (key != started && state.Pulls.TryGetValue(key, out var other) && other.Status == PullStatus.Running)
            {
                // Already under way from earlier: show that one rather than start it twice.
                Props.FocusPull(key);
                setOpen(false);
                return;
            }

            if (running && key == started)
                return;
            seen.Current = false;
            setStarted(key);
            AppServices.Current.Session.Dispatch(new PullRequested(text));
        }

        var invalid = text.Trim().Length > 0 && key is null;
        Element? status = null;
        if (pull is not null)
        {
            var figure = PullFigure.Of(pull);
            status = DialogParts.Stack(
                    8,
                    DialogParts.Message(figure.Text, ended ? Theme.SystemCritical : Theme.SecondaryText).AutomationId("pull-dialog-status"),
                    running ? (figure.Fraction is { } fraction ? Progress(100 * fraction) : ProgressIndeterminate()).AutomationName("Pulling " + pull.Reference) : null,
                    running ? DialogParts.FieldHint("You can close this dialog. The pull goes on in the list.") : null)
                .WithKey("status");
        }

        var content = DialogParts.Stack(
                12,
                Widgets.TextField("", setText, placeholder: Example, header: "Image")
                    .AutomationId("pull-reference")
                    .AutomationName("Image")
                    .WithKey("reference"),
                invalid ? DialogParts.FieldError("Enter an image name, with an optional :tag or @digest.").WithKey("error") : null,
                status)
            .MinWidth(400);

        return (ContentDialog("Pull image", content, ended ? "Try again" : "Pull") with
            {
                IsOpen = open,
                CloseButtonText = running ? "Close" : "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = connected && key is not null && !(running && key == started),
                OnClosed = _ =>
                {
                    // A failure was read here; it does not also stay in the list.
                    if (started is not null && ended)
                        AppServices.Current.Session.Dispatch(new PullDismissed(started));
                    Props.OnDone();
                },
            })
            .KeepOpenOnPrimary(Pull);
    }
}

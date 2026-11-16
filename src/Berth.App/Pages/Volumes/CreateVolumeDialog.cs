using Berth.Components;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Volumes;

/// <param name="OnDone">Called once the dialog has closed.</param>
internal sealed record CreateVolumeDialogProps(Action OnDone);

/// <summary>
/// "Create volume": one field for the name, checked as it is typed (the engine's rule for names, and not a name already listed), its error
/// under it and Create disabled while it is wrong. Create sends it and the dialog waits (Create disabled from the click): it closes when the
/// engine has made the volume, and shows the engine's refusal as it is otherwise (the session's <see cref="EngineState.VolumeCreate"/>).
/// Mounted for one use: it opens when mounted.
/// </summary>
internal sealed class CreateVolumeDialog : EngineStateComponent<CreateVolumeDialogProps>
{
    public override Element Render()
    {
        var state = UseEngineState();
        var (name, setName) = UseState("");
        var (open, setOpen) = UseState(true);
        var (failure, setFailure) = UseState<string?>(null);
        // The name sent, while its create is under way.
        var (sent, setSent) = UseState<string?>(null);
        // From the click until the create has ended, so a second click or Enter before the dialog renders again sends nothing.
        var asked = UseRef(false);
        // The session has shown this create: once it is gone from the state again, the connection changed (which forgets it).
        var seen = UseRef(false);

        var connected = state.Status == ConnectionStatus.Connected;
        var outcome = sent is not null && state.VolumeCreate is { Unattended: false } create && create.Name == sent ? create : null;
        if (outcome is not null)
            seen.Current = true;
        var interrupted = sent is not null && outcome is null && (seen.Current || !connected);
        var ended = interrupted ? "interrupted" : outcome is { Status: not VolumeCreateStatus.InProgress } ? outcome.Status.ToString() : "";

        // A result of an earlier dialog is no longer wanted.
        UseEffect(() => AppServices.Current.Session.Dispatch(new VolumeCreateDismissed()), Array.Empty<object>());

        // The create ended: created closes the dialog; a refusal is shown under the form as the engine gave it, and so is a change of
        // connection that forgot the create.
        UseEffect(
            () =>
            {
                if (ended.Length == 0)
                    return;
                if (outcome is { Status: VolumeCreateStatus.Created })
                {
                    setOpen(false);
                    return;
                }

                asked.Current = false;
                seen.Current = false;
                setSent(null);
                setFailure(outcome?.Error ?? VolumeRows.CreateInterruptedText);
            },
            ended);

        void Create()
        {
            var session = AppServices.Current.Session;
            if (asked.Current || !VolumeRows.CanCreate(name, session.State))
                return;
            asked.Current = true;
            seen.Current = false;
            setFailure(null);
            setSent(name);
            // Forget how an earlier create of this dialog ended first, so its result is never read as this one's.
            session.Dispatch(new VolumeCreateDismissed());
            session.Dispatch(new CreateVolumeRequested(name));
        }

        var error = VolumeRows.NameError(name, state);
        var content = DialogParts.Stack(
                8,
                Widgets.TextField("", value =>
                    {
                        setName(value);
                        setFailure(null);
                    }, placeholder: "my-data", header: "Name")
                    .AutomationId("volume-name")
                    .AutomationName("Name")
                    .IsEnabled(sent is null)
                    .WithKey("name"),
                (error is not null
                    ? DialogParts.FieldError(error).AutomationId("volume-name-error")
                    : DialogParts.FieldHint("Letters, digits, _, . and -, starting with a letter or digit.")).WithKey("note"),
                sent is not null
                    ? DialogParts.Stack(4, DialogParts.FieldHint("Creating…"), ProgressIndeterminate().AutomationName("Creating the volume")).WithKey("creating")
                    : null,
                failure is not null ? DialogParts.Message(failure, Theme.SystemCritical).AutomationId("volume-create-error").WithKey("failure") : null)
            .MinWidth(400);

        return (ContentDialog("Create volume", content, "Create") with
            {
                IsOpen = open,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = sent is null && !asked.Current && VolumeRows.CanCreate(name, state),
                OnClosed = _ =>
                {
                    AppServices.Current.Session.Dispatch(new VolumeCreateDismissed());
                    Props.OnDone();
                },
            })
            .KeepOpenOnPrimary(Create);
    }
}

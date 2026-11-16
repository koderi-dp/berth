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
/// under it and Create disabled while it is wrong. Create sends it under a new request number and the dialog waits (Create disabled from
/// the click): it reads only the session's entry for that request (<see cref="EngineState.VolumeCreate"/>), which is always answered. It
/// closes when the engine has made the volume, and says why otherwise (<see cref="VolumeRows.CreateFailure"/>). Mounted for one use: it
/// opens when mounted.
/// </summary>
internal sealed class CreateVolumeDialog : EngineStateComponent<CreateVolumeDialogProps>
{
    public override Element Render()
    {
        var state = UseEngineState();
        var (name, setName) = UseState("");
        var (open, setOpen) = UseState(true);
        var (failure, setFailure) = UseState<string?>(null);
        // The request of the last Create (0 before the first) and the session it went to.
        var (sentTo, setSentTo) = UseState((Request: 0, Session: 0));
        var request = sentTo.Request;
        // From the click until the create has ended, so a second click or Enter before the dialog renders again sends nothing.
        var asked = UseRef(false);

        // A switch of connection replaces the session, and the new one never heard of the request: it is interrupted.
        var outcome = request == 0 ? null
            : sentTo.Session != AppServices.Current.SessionNumber ? new VolumeCreate(request, name, VolumeCreateStatus.Interrupted)
            : state.VolumeCreate is { } create && create.RequestId == request ? create : null;
        var waiting = asked.Current && outcome is not { HasEnded: true };
        var ended = outcome is { HasEnded: true } ? $"{outcome.RequestId}:{outcome.Status}" : "";

        // The create ended: created closes the dialog; otherwise why is shown under the form.
        UseEffect(
            () =>
            {
                if (outcome is not { HasEnded: true })
                    return;
                if (outcome.Status == VolumeCreateStatus.Created)
                {
                    setOpen(false);
                    return;
                }

                asked.Current = false;
                setFailure(VolumeRows.CreateFailure(outcome));
            },
            ended);

        void Create()
        {
            var session = AppServices.Current.Session;
            if (asked.Current || !VolumeRows.CanCreate(name, session.State))
                return;
            asked.Current = true;
            setFailure(null);
            if (request != 0)
                session.Dispatch(new VolumeCreateDismissed(request));
            var next = AppServices.Current.NextRequestId();
            setSentTo((next, AppServices.Current.SessionNumber));
            session.Dispatch(new CreateVolumeRequested(next, name));
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
                    .IsEnabled(!waiting)
                    .WithKey("name"),
                (error is not null
                    ? DialogParts.FieldError(error).AutomationId("volume-name-error")
                    : DialogParts.FieldHint("Letters, digits, _, . and -, starting with a letter or digit.")).WithKey("note"),
                waiting
                    ? DialogParts.Stack(4, DialogParts.FieldHint("Creating…"), ProgressIndeterminate().AutomationName("Creating the volume")).WithKey("creating")
                    : null,
                failure is not null ? DialogParts.Message(failure, Theme.SystemCritical).AutomationId("volume-create-error").WithKey("failure") : null)
            .MinWidth(400);

        return (ContentDialog("Create volume", content, "Create") with
            {
                IsOpen = open,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = !waiting && VolumeRows.CanCreate(name, state),
                OnClosed = _ =>
                {
                    if (request != 0)
                        AppServices.Current.Session.Dispatch(new VolumeCreateDismissed(request));
                    Props.OnDone();
                },
            })
            .KeepOpenOnPrimary(Create);
    }
}

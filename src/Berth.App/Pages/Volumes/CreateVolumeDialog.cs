using Berth.Components;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Volumes;

/// <param name="OnDone">Called once the dialog has closed.</param>
internal sealed record CreateVolumeDialogProps(Action OnDone);

/// <summary>
/// "Create volume": one field for the name, checked as it is typed (the engine's rule for names, and not a name already listed), its error
/// under it and Create disabled while it is wrong. Create sends it and the dialog waits: it closes when the engine has made the volume, and
/// shows the engine's refusal as it is otherwise. Mounted for one use: it opens when mounted.
/// </summary>
internal sealed class CreateVolumeDialog : EngineStateComponent<CreateVolumeDialogProps>
{
    public override Element Render()
    {
        var state = UseEngineState();
        var (name, setName) = UseState("");
        var (open, setOpen) = UseState(true);
        var (failure, setFailure) = UseState<string?>(null);
        // The name sent, while its create is under way; and whether the session has shown it pending yet.
        var (sent, setSent) = UseState<string?>(null);
        var seen = UseRef(false);
        // The error bar's message when Create was clicked: a different one when the create ends is the engine's refusal of it.
        var errorBefore = UseRef<string?>(null);

        var connected = state.Status == ConnectionStatus.Connected;
        var creating = sent is not null && state.Volumes.Pending.ContainsKey(sent);
        if (creating)
            seen.Current = true;

        // The create ended: the session reads the list again (success), or set the error bar to the engine's message (failure), which is
        // shown here instead and taken off the bar, or the connection went. The page may not render while the create is pending (state
        // changes are coalesced), so the volume listed or a new error also tell that it ended.
        var listed = sent is not null && state.Volumes.Items.Any(v => v.Name == sent);
        var refused = state.ActionError is { } newError && !ReferenceEquals(newError, errorBefore.Current);
        var ended = sent is not null && !creating && (seen.Current || listed || refused || !connected);
        UseEffect(
            () =>
            {
                if (!ended)
                    return;
                seen.Current = false;
                setSent(null);
                if (!connected)
                {
                    setFailure(state.ConnectionError ?? "The connection to the engine was lost.");
                }
                else if (!listed && refused && state.ActionError is { } error)
                {
                    setFailure(error);
                    AppServices.Current.Session.Dispatch(new ActionErrorDismissed());
                }
                else
                {
                    setOpen(false);
                }
            },
            ended);

        void Create()
        {
            var current = AppServices.Current.Session.State;
            if (sent is not null || !VolumeRows.CanCreate(name, current))
                return;
            setFailure(null);
            errorBefore.Current = current.ActionError;
            seen.Current = false;
            setSent(name);
            AppServices.Current.Session.Dispatch(new CreateVolumeRequested(name));
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
                IsPrimaryButtonEnabled = sent is null && VolumeRows.CanCreate(name, state),
                OnClosed = _ => Props.OnDone(),
            })
            .KeepOpenOnPrimary(Create);
    }
}

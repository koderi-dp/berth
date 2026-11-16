using Berth.Components;
using Berth.Hooks;
using Berth.Pages.Detail;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Images;

/// <param name="Name">How the dialog names the image ("alpine:3.19").</param>
/// <param name="ImageId">The image to read.</param>
/// <param name="OnDone">Called once the dialog has closed.</param>
internal sealed record InspectDialogProps(string Name, string ImageId, Action OnDone);

/// <summary>
/// An image's whole inspect answer as the engine gave it, in the same raw JSON view as a container's Inspect tab, with Copy. It reads the
/// image when it opens; a failed read shows the engine's message. Mounted for one use: it opens when mounted.
/// </summary>
internal sealed class InspectDialog : EngineStateComponent<InspectDialogProps>
{
    private const double JsonHeight = 480;
    private const double ContentWidth = 720;

    public override Element Render()
    {
        var imageId = Props.ImageId;
        var state = UseEngineState();
        var connected = state.Status == ConnectionStatus.Connected;
        var inspect = state.ImageInspect is { } read && read.Id == imageId ? read : null;
        var json = inspect?.Inspection?.RawJson;

        // Read the image when the dialog opens, and again after the connection changed (which forgets the read).
        var unread = inspect is null;
        UseEffect(
            () =>
            {
                if (connected && unread)
                    AppServices.Current.Session.Dispatch(new ImageInspectRequested(imageId));
            },
            connected,
            unread);

        // Tens of kilobytes: the view is built once per answer, not on each render (pulls change the state ten times a second).
        var view = UseMemo(() => json is null ? null : InspectTab.RawView(json).Height(JsonHeight).MinWidth(ContentWidth), json ?? "");

        Element content = view
            ?? (inspect?.Error is { } error
                ? DialogParts.Message(error, Theme.SystemCritical).AutomationId("inspect-error")
                : (FlexRow(ProgressRing().Size(16, 16).AutomationName("Reading the image"), Body("Reading the image…").Foreground(Theme.SecondaryText))
                    with
                    {
                        AlignItems = FlexAlign.Center,
                        ColumnGap = 8,
                    }).Height(JsonHeight));

        return (ContentDialog("Inspect " + Props.Name, view is null ? content.MinWidth(ContentWidth) : content, "Copy") with
            {
                IsOpen = true,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = json is not null,
                OnClosed = _ =>
                {
                    AppServices.Current.Session.Dispatch(new ImageInspectDismissed());
                    Props.OnDone();
                },
            })
            .KeepOpenOnPrimary(() =>
            {
                if (json is not null)
                    ContainerParts.CopyToClipboard(json);
            })
            .Wide(960);
    }
}

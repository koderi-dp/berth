using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>What the app's dialogs share: a primary button that acts without closing, a wider dialog, and their message lines.</summary>
internal static class DialogParts
{
    private static readonly ConditionalWeakTable<ContentDialog, PrimaryHandler> Handlers = [];

    /// <summary>
    /// The primary button (and Enter, when it is the default) calls <paramref name="onPrimary"/> and leaves the dialog open, so the
    /// dialog can show what happens next (a run's progress, the engine's refusal). The dialog closes only through its close button,
    /// Escape, or its own <c>IsOpen</c>. The handler is attached once per dialog; each render hands it the latest action.
    /// </summary>
    public static ContentDialogElement KeepOpenOnPrimary(this ContentDialogElement dialog, Action onPrimary) =>
        dialog.Set(control =>
        {
            if (!Handlers.TryGetValue(control, out var handler))
            {
                handler = new PrimaryHandler();
                Handlers.Add(control, handler);
                control.PrimaryButtonClick += (_, args) =>
                {
                    args.Cancel = true;
                    handler.Action?.Invoke();
                };
            }

            handler.Action = onPrimary;
        });

    /// <summary>Lets the dialog grow to <paramref name="maxWidth"/> (WinUI's default is 548), for fields side by side or a JSON view.</summary>
    public static ContentDialogElement Wide(this ContentDialogElement dialog, double maxWidth) =>
        dialog.Set(control => control.Resources["ContentDialogMaxWidth"] = maxWidth);

    /// <summary>
    /// A dialog's parts one under another, <paramref name="gap"/> apart, as tall as they are (a flex column in a dialog took all the
    /// height the dialog offered); null parts are left out. Keep a part's key on it, so a part that comes and goes moves no other.
    /// </summary>
    public static Element Stack(double gap, params Element?[] parts) => VStack(gap, [.. parts.OfType<Element>()]);

    /// <summary>A field's error under it, in the critical colour.</summary>
    public static Element FieldError(string text) =>
        Caption(text).TextWrapping().Foreground(Theme.SystemCritical);

    /// <summary>A field's hint under it, in the secondary colour.</summary>
    public static Element FieldHint(string text) =>
        Caption(text).TextWrapping().Foreground(Theme.SecondaryText);

    /// <summary>The engine's message (or another outcome) in a dialog: selectable, wrapped, and read out when it appears.</summary>
    public static Element Message(string text, ThemeRef color) =>
        Body(text).TextWrapping().IsTextSelectionEnabled().Foreground(color).LiveRegion(AutomationLiveSetting.Polite);

    private sealed class PrimaryHandler
    {
        public Action? Action { get; set; }
    }
}

using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>
/// The confirmation before images, volumes or networks are removed (a delete or "Remove unused …"), and what a confirmed one sends. The
/// text comes from <see cref="Confirmations"/>; nothing is ever forced.
/// </summary>
internal static class RemoveDialog
{
    /// <summary>A confirmation that names what will be removed. Cancel is the default button; Remove is disabled unless <paramref name="canConfirm"/>.</summary>
    public static Element Render(string title, string text, bool canConfirm, Action onConfirm, Action onDone) =>
        ContentDialog(title, Body(text).TextWrapping().IsTextSelectionEnabled().AutomationId("confirm-text"), "Remove") with
        {
            IsOpen = true,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = canConfirm,
            OnClosed = result =>
            {
                if (result == ContentDialogResult.Primary)
                    onConfirm();
                onDone();
            },
        };

    /// <summary>Sends a delete planned again at confirm from the current session; nothing when nothing is left to send.</summary>
    public static void Send(RemovalPlan plan)
    {
        if (!plan.IsEmpty)
            AppServices.Current.Session.Dispatch(plan.ToEvent());
    }

    /// <summary>Sends what a "Remove unused …" dialog named that is still unused, listed and not pending.</summary>
    public static void SendUnused(PrunePlan named)
    {
        var session = AppServices.Current.Session;
        var plan = Confirmations.ConfirmPrune(named, session.State);
        if (!plan.IsEmpty)
            session.Dispatch(plan.ToEvent());
    }
}

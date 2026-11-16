using System.Collections.Immutable;
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
    /// <summary>What a list page's remove confirmation is open for, on the session <paramref name="Session"/> (<see cref="AppServices.SessionNumber"/>).</summary>
    internal abstract record Ask(int Session);

    /// <summary>A delete: the keys the user named, and the plan made from them when the dialog opened (its text).</summary>
    internal sealed record DeleteAsk(ImmutableArray<string> Keys, RemovalPlan Plan, int Session) : Ask(Session);

    /// <summary>A "Remove unused …": the plan made when the dialog opened, which names everything it may remove.</summary>
    internal sealed record UnusedAsk(PrunePlan Plan, int Session) : Ask(Session);

    /// <summary>
    /// The confirmation <paramref name="ask"/> is for, or null when none is open. A confirmed delete is planned again with
    /// <paramref name="planDelete"/> from the session as it is then, so it sends only what is still shown, listed and not pending of
    /// the keys named; a confirmed "Remove unused …" sends what it named that is still unused (<see cref="SendUnused"/>). Nothing is sent
    /// when the connection was switched since the dialog opened: the same names can mean other things on another engine.
    /// </summary>
    public static Element? For(Ask? ask, Func<Session.EngineState, ImmutableArray<string>, RemovalPlan> planDelete, Action onDone) => ask switch
    {
        DeleteAsk delete => Render(
            delete.Plan.Title,
            delete.Plan.Text,
            canConfirm: true,
            () =>
            {
                if (IsCurrent(delete.Session))
                    Send(planDelete(AppServices.Current.Session.State, delete.Keys));
            },
            onDone),
        UnusedAsk unused => Render(
            unused.Plan.Title,
            unused.Plan.Text,
            !unused.Plan.IsEmpty,
            () =>
            {
                if (IsCurrent(unused.Session))
                    SendUnused(unused.Plan);
            },
            onDone),
        _ => null,
    };

    /// <summary>The session <paramref name="session"/> (a <see cref="AppServices.SessionNumber"/>) is still the current one.</summary>
    public static bool IsCurrent(int session) => session == AppServices.Current.SessionNumber;

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

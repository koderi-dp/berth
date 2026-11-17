using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>
/// The confirmation before images, volumes or networks are removed (a delete or "Remove unused …"), and what a confirmed one sends. One
/// item is named in a line of text; several are a table the user can untick rows of (<see cref="RemoveTableDialog"/>). The texts come
/// from <see cref="Confirmations"/> and <see cref="RemoveTable"/>; nothing is ever forced.
/// </summary>
internal static class RemoveDialog
{
    /// <summary>What a list page's remove confirmation is open for, on the session <paramref name="Session"/> (<see cref="AppServices.SessionNumber"/>).</summary>
    internal abstract record Ask(int Session);

    /// <summary>A delete: the keys the user named, and the plan made from them when the dialog opened (its text or table).</summary>
    internal sealed record DeleteAsk(ImmutableArray<string> Keys, RemovalPlan Plan, int Session) : Ask(Session);

    /// <summary>A "Remove unused …": the plan made when the dialog opened, which names everything it may remove.</summary>
    internal sealed record UnusedAsk(PrunePlan Plan, int Session) : Ask(Session);

    /// <summary>
    /// The confirmation <paramref name="ask"/> is for, or null when none is open. A confirmed delete is planned again with
    /// <paramref name="planDelete"/> from the session as it is then, of the keys still checked, so it sends only what is still shown, listed
    /// and not pending; a confirmed "Remove unused …" sends what is still checked and still unused (<see cref="SendUnused"/>). Nothing is
    /// sent when the connection was switched since the dialog opened: the same names can mean other things on another engine.
    /// <paramref name="handlers"/> is the page's one instance, pointed at the latest actions here, so the table's props stay equal.
    /// </summary>
    public static Element? For(
        Ask? ask,
        DiskUsage? usage,
        Func<Session.EngineState, ImmutableArray<string>, RemovalPlan> planDelete,
        Action onDone,
        RemoveTableHandlers handlers)
    {
        handlers.Done = onDone;
        switch (ask)
        {
            case DeleteAsk { Plan.Table: { } table } delete:
                handlers.Confirm = chosen =>
                {
                    if (IsCurrent(delete.Session))
                        Send(planDelete(AppServices.Current.Session.State, table.Keys(chosen)));
                };
                return Component<RemoveTableDialog, RemoveTableDialogProps>(new(delete.Plan.Title, table, usage, handlers));
            case DeleteAsk delete:
                return Render(
                    delete.Plan.Title,
                    delete.Plan.Text,
                    canConfirm: true,
                    () =>
                    {
                        if (IsCurrent(delete.Session))
                            Send(planDelete(AppServices.Current.Session.State, delete.Keys));
                    },
                    onDone);
            case UnusedAsk { Plan: { IsEmpty: false, Table: { } table } } unused:
                handlers.Confirm = chosen =>
                {
                    if (IsCurrent(unused.Session))
                        SendUnused(unused.Plan.Keeping(chosen));
                };
                return Component<RemoveTableDialog, RemoveTableDialogProps>(new(unused.Plan.Title, table, usage, handlers));
            case UnusedAsk unused:
                // Nothing to remove, or what is unused is not known yet: the text says why and Remove stays disabled.
                return Render(unused.Plan.Title, unused.Plan.Text, canConfirm: false, () => { }, onDone);
            default:
                return null;
        }
    }

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

    /// <summary>Sends what a "Remove unused …" dialog named (and the user left checked) that is still unused, listed and not pending.</summary>
    public static void SendUnused(PrunePlan named)
    {
        if (named.IsEmpty)
            return;
        var session = AppServices.Current.Session;
        var plan = Confirmations.ConfirmPrune(named, session.State);
        if (!plan.IsEmpty)
            session.Dispatch(plan.ToEvent());
    }
}

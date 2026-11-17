using Berth.Connections;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <param name="Problem">Why the active SSH connection stopped for the user (<see cref="Session.EngineState.Attention"/>).</param>
internal sealed record SshPromptProps(SshProblem Problem);

/// <summary>
/// The disconnected view's (and the bar's) button for an SSH connection that needs the user, and the dialog it opens: the host key
/// question (Accept saves the key and connects; Cancel leaves it), the changed-key warning (no way to accept it there: the saved key is
/// removed in Settings first), or the password or passphrase prompt ("Remember" on by default). The typed secret lives in a ref only
/// until the dialog closes; it is never in state, the settings or the log.
/// </summary>
internal sealed class SshPromptButton : Component<SshPromptProps>
{
    /// <summary>
    /// The key the views mount it under: the session number and the connection, so a new session (a switch, an edit, an accepted key)
    /// always mounts a new one, dropping an open dialog and a typed secret meant for the old attempt.
    /// </summary>
    public static string Key() => $"ssh-prompt-{AppServices.Current.SessionNumber}-{AppServices.Current.ActiveConnectionProfile.Id}";

    public override Element Render()
    {
        var problem = Props.Problem;
        var (open, setOpen) = UseState(false);
        var (remember, setRemember) = UseState(true);
        var (typed, setTyped) = UseState(false);
        var secret = UseRef("");
        var prompt = SshPrompts.For(problem);
        if (prompt is null)
            return Empty();

        var profile = AppServices.Current.ActiveConnectionProfile;
        void Close()
        {
            secret.Current = "";
            setTyped(false);
            setOpen(false);
        }

        var dialog = !open
            ? null
            : prompt.Kind switch
            {
                SshPromptKind.AcceptHostKey => HostKeyDialog(problem.Offered!, accepted =>
                {
                    Close();
                    if (accepted)
                        AppServices.Current.AcceptHostKey(profile.Id, problem.Offered!);
                }),
                SshPromptKind.ChangedHostKey => ChangedKeyDialog(problem.Saved!, problem.Offered!, Close),
                _ => SecretDialog(
                    SshPrompts.Secret(problem, profile.Ssh?.User ?? "", profile.Host, profile.Port),
                    remember,
                    setRemember,
                    typed,
                    value =>
                    {
                        secret.Current = value;
                        setTyped(value.Length > 0);
                    },
                    connect =>
                    {
                        var value = secret.Current;
                        Close();
                        if (connect && value.Length > 0 && problem.Secret is { } kind)
                            AppServices.Current.EnterSecret(profile.Id, kind, value, remember);
                    }),
            };

        return HStack(
            0,
            [
                Button(prompt.Action, () => setOpen(true)).AccentButton().AutomationId("ssh-prompt"),
                .. dialog is null ? Array.Empty<Element>() : [dialog],
            ]);
    }

    private static Element HostKeyDialog(HostKey offered, Action<bool> onClosed)
    {
        var question = SshPrompts.Question(offered);
        var content = DialogParts.Stack(
            12,
            Body(question.Intro).TextWrapping(),
            KeyBox(question.KeyType + " key", question.Fingerprint, "host-key-fingerprint"),
            DialogParts.FieldHint(question.Advice));
        return ContentDialog(question.Title, content, "Accept") with
        {
            IsOpen = true,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            OnClosed = result => onClosed(result == ContentDialogResult.Primary),
        };
    }

    private static Element ChangedKeyDialog(HostKey saved, HostKey offered, Action onClosed)
    {
        var warning = SshPrompts.Changed(saved, offered);
        var content = DialogParts.Stack(
            12,
            Body(warning.Intro).TextWrapping().LiveRegion(AutomationLiveSetting.Assertive),
            KeyBox("Accepted before", SshPrompts.KeyLine(saved), "host-key-saved"),
            KeyBox("Offered now", SshPrompts.KeyLine(offered), "host-key-offered"),
            DialogParts.FieldHint(warning.Advice));
        return ContentDialog(warning.Title, content, "") with
        {
            IsOpen = true,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            OnClosed = _ => onClosed(),
        };
    }

    private static Element SecretDialog(
        SecretQuestion question, bool remember, Action<bool> setRemember, bool typed, Action<string> onTyped, Action<bool> onClosed)
    {
        var content = DialogParts.Stack(
            12,
            DialogParts.FieldHint(question.Note),
            PasswordBox(default, onTyped).Header(question.Label).AutomationName(question.Label).AutomationId("ssh-secret"),
            CheckBox(remember, setRemember, question.RememberLabel).AutomationId("ssh-remember"));
        return ContentDialog(question.Title, content, "Connect") with
        {
            IsOpen = true,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = typed,
            OnClosed = result => onClosed(result == ContentDialogResult.Primary),
        };
    }

    /// <summary>A key: its label, then the fingerprint in the monospace font, selectable to compare or copy.</summary>
    internal static Element KeyBox(string label, string text, string automationId) =>
        Border(
                DialogParts.Stack(
                    4,
                    Caption(label).Foreground(Theme.SecondaryText),
                    TextBlock(text).FontFamily("Cascadia Mono, Consolas").IsTextSelectionEnabled().TextWrapping().AutomationId(automationId)))
            .Background(Theme.Ref("CardBackgroundFillColorSecondaryBrush"))
            .CornerRadius(4)
            .Padding(12, 8);
}

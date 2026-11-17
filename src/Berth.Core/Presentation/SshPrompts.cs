using Berth.Connections;

namespace Berth.Presentation;

/// <summary>What the user is asked to do about an SSH connection that stopped for them.</summary>
public enum SshPromptKind
{
    /// <summary>A key never seen for this host and port: look at its fingerprint, then Accept or Cancel.</summary>
    AcceptHostKey,

    /// <summary>The host's key is not the one accepted: a warning naming both, with no way to accept it there.</summary>
    ChangedHostKey,

    /// <summary>A password or passphrase to enter (none kept, or the kept one was refused).</summary>
    EnterSecret,
}

/// <summary>What the disconnected view says and offers for an SSH connection that needs the user.</summary>
/// <param name="Title">The view's heading.</param>
/// <param name="Action">The button that opens the dialog.</param>
public sealed record SshPrompt(SshPromptKind Kind, string Title, string Action);

/// <summary>The host key question: what is said before the key, the key, and the advice after it.</summary>
public sealed record HostKeyQuestion(string Title, string Intro, string KeyType, string Fingerprint, string Advice);

/// <summary>The changed-key warning: both keys, and the one way on (remove the saved key in Settings).</summary>
public sealed record ChangedKeyWarning(string Title, string Intro, string SavedLine, string OfferedLine, string Advice);

/// <summary>The password or passphrase prompt.</summary>
/// <param name="Note">Why it is asked: none kept, or the kept one refused (and not tried again).</param>
public sealed record SecretQuestion(string Title, string Label, string Note, string RememberLabel);

/// <summary>The texts of the SSH host key question, the changed-key warning and the secret prompt, and the fingerprint's form.</summary>
public static class SshPrompts
{
    /// <summary>What the disconnected view offers for <paramref name="problem"/>; null when it needs no dialog (Retry is enough).</summary>
    public static SshPrompt? For(SshProblem? problem) => problem switch
    {
        { Failure: SshFailure.HostKeyUnknown, Offered: not null } => new(SshPromptKind.AcceptHostKey, "Check the host's key", "Check the key…"),
        { Failure: SshFailure.HostKeyChanged, Offered: not null, Saved: not null } => new(SshPromptKind.ChangedHostKey, "The host's key has changed", "Show the warning…"),
        { Failure: SshFailure.SecretMissing or SshFailure.SignInRefused, Secret: SecretKind.Password } => new(SshPromptKind.EnterSecret, "Sign-in needed", "Enter password…"),
        { Failure: SshFailure.SecretMissing or SshFailure.SignInRefused, Secret: SecretKind.Passphrase } => new(SshPromptKind.EnterSecret, "Sign-in needed", "Enter passphrase…"),
        _ => null,
    };

    /// <summary>The key type as <c>ssh-keygen -l</c> names it in its last column: ED25519, ECDSA, RSA; anything else as the host named it.</summary>
    public static string KeyType(string type) => type switch
    {
        "ssh-ed25519" => "ED25519",
        "ssh-rsa" => "RSA",
        _ when type.StartsWith("ecdsa-sha2-", StringComparison.Ordinal) => "ECDSA",
        "ssh-dss" => "DSA",
        _ => type,
    };

    /// <summary>"ED25519 key SHA256:…": the type and the fingerprint in the form <c>ssh-keygen -lf</c> prints it.</summary>
    public static string KeyLine(HostKey key) => $"{KeyType(key.Type)} key {key.Fingerprint}";

    /// <summary>"host" for port 22, "host port 2222" otherwise.</summary>
    public static string HostName(string host, int port) => port == EngineAddress.DefaultSshPort ? host : $"{host} port {port}";

    public static HostKeyQuestion Question(HostKey offered) => new(
        "Trust this host?",
        $"Berth has not connected to {HostName(offered.Host, offered.Port)} before. It offered this key:",
        KeyType(offered.Type),
        offered.Fingerprint,
        "Accept only if this matches the fingerprint the host's owner gives you (ssh-keygen -lf on the host's public key). " +
        "Nothing has been sent to the host yet: not your user name, not your password.");

    public static ChangedKeyWarning Changed(HostKey saved, HostKey offered) => new(
        "The host's key has changed",
        $"{HostName(offered.Host, offered.Port)} offered a different key from the one you accepted. This happens when the host was " +
        "reinstalled or its keys were replaced, and also when someone is in between you and the host. Berth will not connect and sent no password.",
        "Accepted before: " + KeyLine(saved),
        "Offered now: " + KeyLine(offered),
        "If you know why the key changed, remove the saved key in Settings (edit the connection), then connect again and check the new key.");

    /// <summary>The prompt for the password of <paramref name="user"/> or the passphrase of the key file.</summary>
    public static SecretQuestion Secret(SshProblem problem, string user, string host, int port)
    {
        var passphrase = problem.Secret == SecretKind.Passphrase;
        var where = $"{user}@{HostName(host, port)}";
        var note = (problem.Failure, passphrase) switch
        {
            (SshFailure.SignInRefused, false) => $"The host refused the password for {where}. Berth did not try it again.",
            (SshFailure.SignInRefused, true) => "The passphrase does not open the key file. Berth did not try it again.",
            (_, false) => $"Berth has no password kept for {where}.",
            _ => "The key file is protected by a passphrase, and Berth has none kept for it.",
        };
        return passphrase
            ? new SecretQuestion("Enter the passphrase", "Passphrase of the key file", note, "Remember the passphrase in Windows Credential Manager")
            : new SecretQuestion("Enter the password", "Password", note, "Remember the password in Windows Credential Manager");
    }
}

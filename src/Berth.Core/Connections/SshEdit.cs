using System.Collections.Immutable;

namespace Berth.Connections;

/// <summary>What saving an edited connection does to the SSH secrets and host keys it kept, and which <see cref="Profile"/> to save.</summary>
/// <param name="DeleteSecrets">The kept secrets to delete before a newly typed one is saved.</param>
public sealed record SshSave(ConnectionProfile Profile, ImmutableArray<SecretKind> DeleteSecrets);

/// <summary>
/// Saved secrets and accepted host keys belong to the address they were kept for, not only to the connection: after its host, port or
/// user changes, the old host's password must never be sent to the new one, and the old host's key says nothing about the new one.
/// </summary>
public static class SshEdit
{
    /// <summary>What the editor says before Save when the address of an SSH connection changed.</summary>
    public const string AddressChangedNote = "The saved password and host key are for the old address and will be removed.";

    private static readonly ImmutableArray<SecretKind> Both = [SecretKind.Password, SecretKind.Passphrase];

    /// <summary>Whether <paramref name="edited"/> is the SSH connection <paramref name="editing"/> moved to another host, port or user.</summary>
    public static bool AddressChanged(ConnectionProfile? editing, ConnectionProfile edited) =>
        editing is { Kind: TransportKind.Ssh, Ssh: { } before }
        && edited is { Kind: TransportKind.Ssh, Ssh: { } after }
        && (!string.Equals(editing.Host, edited.Host, StringComparison.OrdinalIgnoreCase) || editing.Port != edited.Port || before.User != after.User);

    /// <summary>Whether a test of <paramref name="edited"/> may sign in with the secret kept for <paramref name="editing"/>: same SSH address only.</summary>
    public static bool MayUseSavedSecret(ConnectionProfile? editing, ConnectionProfile edited) =>
        editing is { Kind: TransportKind.Ssh } && edited.Kind == TransportKind.Ssh && !AddressChanged(editing, edited);

    /// <summary>
    /// A new address drops the kept secrets and every host key saved before (a key accepted for the new address during the edit stays); an SSH connection made into another kind drops its secrets; a
    /// switch between password and key file drops the secret of the one no longer used. Anything else keeps everything.
    /// </summary>
    public static SshSave Save(ConnectionProfile? editing, ConnectionProfile edited)
    {
        if (editing is not { Kind: TransportKind.Ssh, Ssh: { } before })
            return new SshSave(edited, []);
        if (edited is not { Kind: TransportKind.Ssh, Ssh: { } after })
            return new SshSave(edited, Both);
        if (AddressChanged(editing, edited))
        {
            // Only a key accepted during this edit for the new host and port stays: every key saved before is the old address's.
            ImmutableArray<HostKey> accepted = [.. after.KnownHosts.Where(key => key.IsFor(edited.Host, edited.Port) && !before.KnownHosts.Contains(key))];
            return new SshSave(edited with { Ssh = after with { KnownHosts = accepted } }, Both);
        }
        if (before.SignIn != after.SignIn)
            return new SshSave(edited, [after.SignIn == SshSignIn.Password ? SecretKind.Passphrase : SecretKind.Password]);
        return new SshSave(edited, []);
    }
}

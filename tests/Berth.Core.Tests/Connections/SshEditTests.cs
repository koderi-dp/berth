using Berth.Connections;

namespace Berth.Tests.Connections;

public sealed class SshEditTests
{
    private static readonly HostKey Key = new("host", 22, "ssh-ed25519", "SHA256:abc");

    private static readonly ConnectionProfile Saved = new("id", "Remote", TransportKind.Ssh, "host", 22, "", null)
    {
        Ssh = new SshOptions("berth", SshSignIn.Password, null, [Key]),
    };

    public static TheoryData<ConnectionProfile> NewAddresses =>
    [
        Saved with { Host = "other-host" },
        Saved with { Port = 2222 },
        Saved with { Ssh = Saved.Ssh! with { User = "someone" } },
    ];

    [Theory]
    [MemberData(nameof(NewAddresses))]
    public void A_new_host_port_or_user_drops_the_saved_secrets_and_host_keys_and_never_tests_with_them(ConnectionProfile edited)
    {
        var save = SshEdit.Save(Saved, edited);

        Assert.True(SshEdit.AddressChanged(Saved, edited));
        Assert.False(SshEdit.MayUseSavedSecret(Saved, edited));
        Assert.Empty(save.Profile.Ssh!.KnownHosts);
        Assert.Equal<SecretKind>([SecretKind.Password, SecretKind.Passphrase], save.DeleteSecrets);
    }

    [Fact]
    public void The_same_address_keeps_everything_even_with_the_host_name_in_other_case()
    {
        var edited = Saved with { Name = "Renamed", Host = "HOST" };

        var save = SshEdit.Save(Saved, edited);

        Assert.False(SshEdit.AddressChanged(Saved, edited));
        Assert.True(SshEdit.MayUseSavedSecret(Saved, edited));
        Assert.Same(edited, save.Profile);
        Assert.Empty(save.DeleteSecrets);
    }

    [Fact]
    public void Switching_the_sign_in_deletes_the_secret_of_the_other_one()
    {
        var byKey = Saved with { Ssh = Saved.Ssh! with { SignIn = SshSignIn.KeyFile, KeyPath = @"C:\keys\id" } };

        Assert.Equal<SecretKind>([SecretKind.Password], SshEdit.Save(Saved, byKey).DeleteSecrets);
        Assert.Equal<SecretKind>([SecretKind.Passphrase], SshEdit.Save(byKey, Saved).DeleteSecrets);
        Assert.Equal<HostKey>([Key], SshEdit.Save(Saved, byKey).Profile.Ssh!.KnownHosts);
    }

    [Fact]
    public void A_new_connection_or_one_that_is_no_longer_ssh()
    {
        var tcp = new ConnectionProfile("id", "Remote", TransportKind.Tcp, "host", 2375, "", null);

        Assert.False(SshEdit.MayUseSavedSecret(null, Saved));
        Assert.Empty(SshEdit.Save(null, Saved).DeleteSecrets);
        Assert.Equal<SecretKind>([SecretKind.Password, SecretKind.Passphrase], SshEdit.Save(Saved, tcp).DeleteSecrets);
        Assert.Empty(SshEdit.Save(tcp, tcp).DeleteSecrets);
        Assert.False(SshEdit.AddressChanged(tcp, Saved));
    }
}

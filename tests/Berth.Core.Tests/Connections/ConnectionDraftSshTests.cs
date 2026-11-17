using Berth.Connections;

namespace Berth.Tests.Connections;

public sealed class ConnectionDraftSshTests
{
    private static readonly HostKey Key = new("host", 2222, "ssh-ed25519", "SHA256:abc");

    private static readonly ConnectionDraft Ssh = new("Remote", "ssh://berth@host:2222", false, "", "", "");

    [Fact]
    public void An_ssh_address_builds_an_ssh_profile_signing_in_by_password()
    {
        Assert.True(Ssh.TryBuild("id", out var profile, out var error), error);

        Assert.Equal(TransportKind.Ssh, profile.Kind);
        Assert.Equal(("host", 2222, ""), (profile.Host, profile.Port, profile.PipeName));
        Assert.Equal(new SshOptions("berth", SshSignIn.Password, null, []), profile.Ssh);
        Assert.Null(profile.Tls);
        Assert.True(Ssh.IsSsh);
    }

    [Fact]
    public void Key_file_sign_in_needs_the_key_file()
    {
        var draft = Ssh with { SignIn = SshSignIn.KeyFile };

        Assert.False(draft.TryBuild("id", out _, out var error));
        Assert.Equal(ConnectionDraft.KeyFileMissing, error);

        Assert.True((draft with { SshKeyPath = @" C:\keys\id_ed25519 " }).TryBuild("id", out var profile, out _));
        Assert.Equal(new SshOptions("berth", SshSignIn.KeyFile, @"C:\keys\id_ed25519", []), profile.Ssh);
    }

    [Fact]
    public void Password_sign_in_keeps_no_key_file()
    {
        var draft = Ssh with { SshKeyPath = @"C:\keys\id" };

        Assert.True(draft.TryBuild("id", out var profile, out _));
        Assert.Null(profile.Ssh!.KeyPath);
    }

    [Fact]
    public void Engine_control_and_tls_are_not_saved_for_ssh()
    {
        var draft = (Ssh with { UseTls = true, CaPath = "" }).WithEngineControl(EngineControl.WslDefault("Ubuntu"));

        Assert.True(draft.TryBuild("id", out var profile, out _));
        Assert.Null(profile.EngineControl);
        Assert.Null(profile.Tls);
        Assert.False(draft.EngineControlAvailable);
        Assert.Null(draft.StartCommandError);
    }

    [Fact]
    public void An_edit_keeps_the_saved_host_keys_and_can_remove_one()
    {
        var saved = new ConnectionProfile("id", "Remote", TransportKind.Ssh, "host", 2222, "", null)
        {
            Ssh = new SshOptions("berth", SshSignIn.KeyFile, @"C:\keys\id", [Key]),
        };

        var draft = ConnectionDraft.From(saved);

        Assert.Equal("ssh://berth@host:2222", draft.Address);
        Assert.Equal(SshSignIn.KeyFile, draft.SignIn);
        Assert.Equal(@"C:\keys\id", draft.SshKeyPath);
        Assert.True(draft.TryBuild("id", out var rebuilt, out _));
        Assert.Equal(saved, rebuilt);

        Assert.True(draft.WithoutHostKey(Key).TryBuild("id", out var removed, out _));
        Assert.Empty(removed.Ssh!.KnownHosts);
    }

    [Fact]
    public void A_tcp_profile_has_no_ssh_options()
    {
        Assert.True(new ConnectionDraft("Local", "tcp://127.0.0.1:2375", false, "", "", "").TryBuild("id", out var profile, out _));

        Assert.Null(profile.Ssh);
    }
}

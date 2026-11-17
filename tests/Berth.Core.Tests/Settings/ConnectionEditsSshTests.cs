using Berth.Connections;
using Berth.Settings;

namespace Berth.Tests.Settings;

public sealed class ConnectionEditsSshTests
{
    private static readonly HostKey Accepted = new("host", 2222, "ssh-ed25519", "SHA256:one");
    private static readonly HostKey Changed = new("host", 2222, "ssh-ed25519", "SHA256:two");

    private static readonly ConnectionProfile Remote = new("remote", "Remote", TransportKind.Ssh, "host", 2222, "", null)
    {
        Ssh = new SshOptions("berth", SshSignIn.Password, null, []),
    };

    private static readonly ConnectionProfile Local = new("local", "Local", TransportKind.Tcp, "127.0.0.1", 2375, "", null);

    private static AppSettings With(params ConnectionProfile[] connections) => new(connections, connections[0].Id, ContainerListOptions.Default);

    [Fact]
    public void Accepting_a_host_key_saves_it_on_its_connection()
    {
        var settings = ConnectionEdits.AcceptHostKey(With(Remote, Local), "remote", Accepted);

        Assert.Equal<HostKey>([Accepted], settings.Connections[0].Ssh!.KnownHosts);
        Assert.Same(Local, settings.Connections[1]);
    }

    [Fact]
    public void A_changed_key_is_never_saved_over_the_accepted_one()
    {
        var accepted = ConnectionEdits.AcceptHostKey(With(Remote), "remote", Accepted);

        Assert.Same(accepted, ConnectionEdits.AcceptHostKey(accepted, "remote", Changed));
    }

    [Fact]
    public void A_key_for_another_host_or_a_connection_that_is_not_ssh_changes_nothing()
    {
        var settings = With(Remote, Local);

        Assert.Same(settings, ConnectionEdits.AcceptHostKey(settings, "local", Accepted));
        Assert.Same(settings, ConnectionEdits.AcceptHostKey(settings, "remote", Accepted with { Host = "elsewhere" }));
        Assert.Same(settings, ConnectionEdits.AcceptHostKey(settings, "gone", Accepted));
    }
}

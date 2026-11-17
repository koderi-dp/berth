using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Berth.Connections;

namespace Berth.Tests.Integration;

/// <summary>
/// A throwaway SSH host for the SSH tests: a container named <c>berth-it-…</c> from an image tagged <c>berth-it-ssh-…</c>, built here
/// from alpine with openssh-server (and the docker CLI unless asked otherwise), the engine's socket mounted in, its port published on
/// 127.0.0.1 only. User <c>berth</c> may use the engine, user <c>nodocker</c> may not; both sign in with a password made for this run,
/// and <c>berth</c> also with two key pairs made at build time (one without a passphrase, one with). The host keys are new for each
/// container. Disposing removes the container, the image and the build folder.
/// </summary>
internal sealed class SshStandIn : IAsyncDisposable
{
    private const string Dockerfile = """
        FROM alpine:3.20
        ARG WITH_CLI=1
        RUN apk add --no-cache openssh-server && if [ "$WITH_CLI" = 1 ]; then apk add --no-cache docker-cli; fi \
         && adduser -D -s /bin/sh berth && adduser -D -s /bin/sh nodocker
        COPY chpasswd.txt passphrase.txt entrypoint.sh /tmp/setup/
        RUN chpasswd < /tmp/setup/chpasswd.txt \
         && mkdir -p /keys /home/berth/.ssh \
         && ssh-keygen -q -t ed25519 -N "" -f /keys/plain \
         && ssh-keygen -q -t ed25519 -N "$(cat /tmp/setup/passphrase.txt)" -f /keys/locked \
         && cat /keys/plain.pub /keys/locked.pub > /home/berth/.ssh/authorized_keys \
         && chown -R berth:berth /home/berth/.ssh && chmod 700 /home/berth/.ssh && chmod 600 /home/berth/.ssh/authorized_keys \
         && mv /tmp/setup/entrypoint.sh /entrypoint.sh && chmod +x /entrypoint.sh && rm -rf /tmp/setup
        ENTRYPOINT ["/entrypoint.sh"]
        """;

    private const string Entrypoint = """
        #!/bin/sh
        set -e
        ssh-keygen -A >/dev/null
        {
          echo "PasswordAuthentication yes"
          echo "PermitRootLogin no"
          echo "MaxSessions ${MAX_SESSIONS:-10}"
          echo "AllowTcpForwarding no"
          echo "LogLevel VERBOSE"
        } >> /etc/ssh/sshd_config
        if [ -S /var/run/docker.sock ]; then
          gid=$(stat -c %g /var/run/docker.sock)
          name=$(getent group "$gid" | cut -d: -f1)
          if [ -z "$name" ]; then addgroup -g "$gid" dockersock; name=dockersock; fi
          addgroup berth "$name"
        fi
        exec /usr/sbin/sshd -D -e
        """;

    private readonly DockerCli _cli;
    private readonly string _folder;
    private readonly List<string> _containers = [];

    private SshStandIn(DockerCli cli, string folder, string image, string password, string passphrase)
    {
        _cli = cli;
        _folder = folder;
        Image = image;
        Password = password;
        Passphrase = passphrase;
    }

    public string Image { get; }

    /// <summary>The password of both users, made for this run.</summary>
    public string Password { get; }

    /// <summary>The passphrase of the locked key, made for this run.</summary>
    public string Passphrase { get; }

    /// <summary>The container now running, its port on 127.0.0.1.</summary>
    public string Container { get; private set; } = "";

    public int Port { get; private set; }

    /// <summary>The private key without a passphrase, saved in the build folder.</summary>
    public string PlainKeyPath => Path.Combine(_folder, "plain");

    /// <summary>The private key with <see cref="Passphrase"/>, saved in the build folder.</summary>
    public string LockedKeyPath => Path.Combine(_folder, "locked");

    /// <summary>Builds the image (with the docker CLI unless <paramref name="withCli"/> is false) and starts a container from it.</summary>
    public static async Task<SshStandIn> StartAsync(DockerCli cli, bool withCli = true, int maxSessions = 10)
    {
        var folder = Directory.CreateTempSubdirectory("berth-it-ssh-").FullName;
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var passphrase = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var image = $"berth-it-ssh-{Guid.NewGuid():N}:test";
        await File.WriteAllTextAsync(Path.Combine(folder, "Dockerfile"), Dockerfile.ReplaceLineEndings("\n"));
        await File.WriteAllTextAsync(Path.Combine(folder, "entrypoint.sh"), Entrypoint.ReplaceLineEndings("\n") + "\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "chpasswd.txt"), $"berth:{password}\nnodocker:{password}\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "passphrase.txt"), passphrase);

        var standIn = new SshStandIn(cli, folder, image, password, passphrase);
        try
        {
            await cli.BuildAsync(image, folder, $"WITH_CLI={(withCli ? 1 : 0)}");
            await standIn.RunAsync(maxSessions, port: null);
            await File.WriteAllTextAsync(standIn.PlainKeyPath, await cli.RunAsync("exec", standIn.Container, "cat", "/keys/plain") + "\n");
            await File.WriteAllTextAsync(standIn.LockedKeyPath, await cli.RunAsync("exec", standIn.Container, "cat", "/keys/locked") + "\n");
            return standIn;
        }
        catch
        {
            await standIn.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Removes the container and starts a new one from the same image on the same port: the host has new host keys (a changed key), or
    /// a lower <paramref name="maxSessions"/>.
    /// </summary>
    public async Task RecreateAsync(int maxSessions = 10)
    {
        await _cli.RemoveAsync(Container);
        await RunAsync(maxSessions, Port);
    }

    public Task StopAsync() => _cli.RunAsync("stop", "-t", "1", Container);

    /// <summary>Gives <c>berth</c> a new random password made inside the container: the one Berth holds is now wrong.</summary>
    public Task ChangePasswordAsync() =>
        _cli.RunAsync("exec", Container, "sh", "-c", "echo \"berth:$(head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n')\" | chpasswd");

    /// <summary>A profile for this host as <paramref name="user"/>, with no saved host key.</summary>
    public ConnectionProfile Profile(string user = "berth", SshSignIn signIn = SshSignIn.Password, string? keyPath = null) =>
        new("it-" + Container, "Stand-in", TransportKind.Ssh, "127.0.0.1", Port, "", null) { Ssh = new SshOptions(user, signIn, keyPath, []) };

    /// <summary>The host's key of <paramref name="type"/> as <c>ssh-keygen -lf</c> prints its fingerprint ("SHA256:…").</summary>
    public async Task<string> FingerprintAsync(string type)
    {
        var file = type switch
        {
            "ssh-ed25519" => "ssh_host_ed25519_key.pub",
            "ecdsa-sha2-nistp256" => "ssh_host_ecdsa_key.pub",
            _ => "ssh_host_rsa_key.pub",
        };
        var line = await _cli.RunAsync("exec", Container, "ssh-keygen", "-lf", "/etc/ssh/" + file);
        return line.Split(' ')[1];
    }

    /// <summary>sshd's log so far: what reached it from Berth (sign-in attempts, user names).</summary>
    public async Task<string> LogAsync()
    {
        var (_, output, error) = await _cli.TryRunAsync("logs", Container);
        return output + "\n" + error;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers)
            await _cli.RemoveAsync(container);
        await _cli.TryRunAsync("image", "rm", Image);
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A key file still open elsewhere; the temp folder is the system's to clear.
        }
    }

    private async Task RunAsync(int maxSessions, int? port)
    {
        Container = DockerCli.NewContainerName();
        _containers.Add(Container);
        var publish = port is { } fixedPort ? $"127.0.0.1:{fixedPort}:22" : "127.0.0.1::22";
        await _cli.RunAsync(
            "run", "-d", "--name", Container, "-p", publish, "-e", $"MAX_SESSIONS={maxSessions}",
            "-v", "/var/run/docker.sock:/var/run/docker.sock", Image);
        var mapped = await _cli.RunAsync("port", Container, "22/tcp");
        Port = int.Parse(mapped.Split('\n')[0].Trim().Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
        await WaitForBannerAsync();
    }

    /// <summary>Waits until sshd sends its banner, at most a minute.</summary>
    private async Task WaitForBannerAsync()
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", Port, patience.Token);
                var buffer = new byte[64];
                using var read = CancellationTokenSource.CreateLinkedTokenSource(patience.Token);
                read.CancelAfter(TimeSpan.FromSeconds(2));
                var count = await client.GetStream().ReadAsync(buffer, read.Token);
                if (Encoding.ASCII.GetString(buffer, 0, count).StartsWith("SSH-", StringComparison.Ordinal))
                    return;
            }
            catch (Exception) when (!patience.IsCancellationRequested)
            {
            }

            await Task.Delay(200, patience.Token);
        }
    }
}

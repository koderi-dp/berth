using System.Text;
using Berth.Connections;
using Berth.Engine;
using Berth.Engine.Models;
using Berth.Transport;

namespace Berth.Tests.Integration;

/// <summary>
/// The SSH transport against a throwaway SSH host (<see cref="SshStandIn"/>) whose docker CLI reaches the engine at <c>BERTH_ENGINE</c>.
/// Each test builds and removes its own host; nothing else is touched.
/// </summary>
public sealed class SshIntegrationTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [EngineFact]
    public async Task Dial_stdio_on_an_ssh_channel_answers_ping()
    {
        await using var host = await SshStandIn.StartAsync(Cli());
        using var cts = new CancellationTokenSource(Patience);
        await using var transport = await AcceptedAsync(host, host.Profile(), host.Password, cts.Token);

        await using var stream = await transport.OpenAsync(cts.Token);
        await stream.WriteAsync("GET /_ping HTTP/1.1\r\nHost: docker\r\n\r\n"u8.ToArray(), cts.Token);
        var answer = await ReadUntilAsync(stream, "\r\n\r\nOK", cts.Token);

        Assert.StartsWith("HTTP/1.1 200 OK", answer, StringComparison.Ordinal);
    }

    [EngineFact]
    public async Task An_unknown_host_key_is_asked_about_with_its_fingerprint_and_no_sign_in_is_tried()
    {
        await using var host = await SshStandIn.StartAsync(Cli());
        using var cts = new CancellationTokenSource(Patience);
        var secrets = new FakeSecrets();
        secrets.Set(host.Profile().Id, SecretKind.Password, host.Password);
        await using var transport = new SshTransport(host.Profile(), secrets);

        var ex = await Assert.ThrowsAsync<SshTransportException>(async () => await transport.OpenAsync(cts.Token));

        Assert.Equal(SshFailure.HostKeyUnknown, ex.Problem.Failure);
        var offered = ex.Problem.Offered!;
        Assert.Equal(("127.0.0.1", host.Port), (offered.Host, offered.Port));
        Assert.Equal(await host.FingerprintAsync(offered.Type), offered.Fingerprint);
        Assert.Equal(0, secrets.Reads);
        var log = await host.LogAsync();
        Assert.DoesNotContain("berth", log, StringComparison.Ordinal);
        Assert.DoesNotContain("password", log, StringComparison.OrdinalIgnoreCase);
    }

    [EngineFact]
    public async Task The_engine_client_lists_the_same_containers_over_ssh_as_the_cli()
    {
        await using var host = await SshStandIn.StartAsync(Cli());
        using var cts = new CancellationTokenSource(Patience);
        await using var transport = await AcceptedAsync(host, host.Profile(), host.Password, cts.Token);
        await using var client = new EngineClient(EngineHttp.CreateHandler(transport), transport);

        await client.ConnectAsync(cts.Token);
        var listed = await client.ListContainersAsync(cts.Token);
        var cli = await Cli().RunAsync("ps", "-a", "-q", "--no-trunc");

        Assert.Equal(cli.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(), listed.Select(c => c.Id).Order());
    }

    [EngineFact]
    public async Task A_wrong_password_is_refused_once_and_said_so()
    {
        await using var host = await SshStandIn.StartAsync(Cli());
        using var cts = new CancellationTokenSource(Patience);
        await using var transport = await AcceptedAsync(host, host.Profile(), "not-" + host.Password, cts.Token);

        var ex = await Assert.ThrowsAsync<SshTransportException>(async () => await transport.OpenAsync(cts.Token));

        Assert.Equal(SshFailure.SignInRefused, ex.Problem.Failure);
        Assert.Equal(SecretKind.Password, ex.Problem.Secret);
        Assert.True(ex.Problem.StopsRetry);
        var log = await host.LogAsync();
        Assert.Equal(1, Count(log, "Failed password for berth"));
    }

    [EngineFact]
    public async Task Key_files_sign_in_with_and_without_a_passphrase_and_a_wrong_or_missing_one_is_told_apart()
    {
        await using var host = await SshStandIn.StartAsync(Cli());
        using var cts = new CancellationTokenSource(Patience);

        await using (var plain = await AcceptedAsync(host, host.Profile(signIn: SshSignIn.KeyFile, keyPath: host.PlainKeyPath), null, cts.Token))
            await PingAsync(plain, cts.Token);

        var locked = host.Profile(signIn: SshSignIn.KeyFile, keyPath: host.LockedKeyPath);
        await using (var withPassphrase = await AcceptedAsync(host, locked, null, cts.Token, passphrase: host.Passphrase))
            await PingAsync(withPassphrase, cts.Token);

        await using (var missing = await AcceptedAsync(host, locked, null, cts.Token))
        {
            var ex = await Assert.ThrowsAsync<SshTransportException>(async () => await missing.OpenAsync(cts.Token));
            Assert.Equal((SshFailure.SecretMissing, SecretKind.Passphrase), (ex.Problem.Failure, ex.Problem.Secret!.Value));
        }

        await using (var wrong = await AcceptedAsync(host, locked, null, cts.Token, passphrase: "not-" + host.Passphrase))
        {
            var ex = await Assert.ThrowsAsync<SshTransportException>(async () => await wrong.OpenAsync(cts.Token));
            Assert.Equal((SshFailure.SignInRefused, SecretKind.Passphrase), (ex.Problem.Failure, ex.Problem.Secret!.Value));
        }

        // A public key with a passphrase kept is a wrong file, not a wrong passphrase.
        var publicKey = host.PlainKeyPath + ".pub";
        await File.WriteAllTextAsync(publicKey, "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI berth-it" + Environment.NewLine, cts.Token);
        await using (var notAKey = await AcceptedAsync(host, host.Profile(signIn: SshSignIn.KeyFile, keyPath: publicKey), null, cts.Token, passphrase: host.Passphrase))
        {
            var ex = await Assert.ThrowsAsync<SshTransportException>(async () => await notAKey.OpenAsync(cts.Token));
            Assert.Equal(SshFailure.KeyFileUnreadable, ex.Problem.Failure);
        }
    }

    [EngineFact]
    public async Task A_changed_host_key_blocks_before_any_sign_in()
    {
        await using var host = await SshStandIn.StartAsync(Cli());
        using var cts = new CancellationTokenSource(Patience);
        var accepted = (await ProbeAsync(host, cts.Token))!;
        await host.RecreateAsync();
        var profile = host.Profile() with { Ssh = host.Profile().Ssh!.Accept(accepted) };
        var secrets = new FakeSecrets();
        secrets.Set(profile.Id, SecretKind.Password, host.Password);
        await using var transport = new SshTransport(profile, secrets);

        var ex = await Assert.ThrowsAsync<SshTransportException>(async () => await transport.OpenAsync(cts.Token));

        Assert.Equal(SshFailure.HostKeyChanged, ex.Problem.Failure);
        Assert.Equal(accepted, ex.Problem.Saved);
        Assert.Equal(await host.FingerprintAsync(ex.Problem.Offered!.Type), ex.Problem.Offered.Fingerprint);
        Assert.NotEqual(accepted.Fingerprint, ex.Problem.Offered.Fingerprint);
        Assert.DoesNotContain("password", await host.LogAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [EngineFact]
    public async Task A_user_who_may_not_use_docker_gets_the_remote_s_message()
    {
        await using var host = await SshStandIn.StartAsync(Cli());
        using var cts = new CancellationTokenSource(Patience);
        await using var transport = await AcceptedAsync(host, host.Profile(user: "nodocker"), host.Password, cts.Token);
        await using var client = new EngineClient(EngineHttp.CreateHandler(transport), transport);

        var ex = await Assert.ThrowsAsync<EngineException>(() => client.ConnectAsync(cts.Token));

        Assert.Equal(SshFailure.DockerNotPermitted, ex.Ssh?.Failure);
        Assert.Contains("permission denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [EngineFact]
    public async Task A_host_without_the_docker_cli_gets_the_remote_s_message()
    {
        await using var host = await SshStandIn.StartAsync(Cli(), withCli: false);
        using var cts = new CancellationTokenSource(Patience);
        await using var transport = await AcceptedAsync(host, host.Profile(), host.Password, cts.Token);
        await using var client = new EngineClient(EngineHttp.CreateHandler(transport), transport);

        var ex = await Assert.ThrowsAsync<EngineException>(() => client.ConnectAsync(cts.Token));

        Assert.Equal(SshFailure.NoDockerCli, ex.Ssh?.Failure);
        Assert.Contains("docker: not found", ex.Message, StringComparison.Ordinal);
    }

    [EngineFact]
    public async Task An_exec_runs_over_ssh_and_its_write_side_closes_alone()
    {
        await using var host = await SshStandIn.StartAsync(Cli());
        var name = DockerCli.NewContainerName();
        try
        {
            await Cli().RunAsync(name);
            using var cts = new CancellationTokenSource(Patience);
            await using var transport = await AcceptedAsync(host, host.Profile(), host.Password, cts.Token);
            await using var client = new EngineClient(EngineHttp.CreateHandler(transport), transport);
            await client.ConnectAsync(cts.Token);

            var exec = await client.CreateExecAsync(name, ["cat"], new TerminalSize(80, 24), [], cts.Token);
            await using var stream = await client.StartExecAsync(exec, new TerminalSize(80, 24), cts.Token);
            await stream.WriteAsync("over ssh\r"u8.ToArray(), cts.Token);
            await ReadUntilAsync(stream, "over ssh", cts.Token);
            await stream.WriteAsync(new byte[] { 4 }, cts.Token);

            Assert.True(await stream.CloseWriteAsync());
            var buffer = new byte[4096];
            while (await stream.ReadAsync(buffer, cts.Token) > 0)
            {
            }

            var inspection = await client.InspectExecAsync(exec, cts.Token);
            Assert.False(inspection.Running);
        }
        finally
        {
            await Cli().RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Logs_stats_a_file_both_ways_and_a_pull_work_over_ssh()
    {
        await using var host = await SshStandIn.StartAsync(Cli());
        var name = DockerCli.NewContainerName();
        try
        {
            await Cli().RunAsync("run", "-d", "--name", name, "alpine:latest", "sh", "-c", "echo hello-over-ssh; sleep 300");
            using var cts = new CancellationTokenSource(Patience);
            await using var transport = await AcceptedAsync(host, host.Profile(), host.Password, cts.Token);
            await using var client = new EngineClient(EngineHttp.CreateHandler(transport), transport);
            await client.ConnectAsync(cts.Token);

            var lines = new List<string>();
            await foreach (var line in client.StreamLogsAsync(name, tty: false, new LogRequest(10, Follow: false), cts.Token))
                lines.Add(line.Text);
            Assert.Contains("hello-over-ssh", lines);

            await foreach (var sample in client.StreamStatsAsync(name, cts.Token))
            {
                Assert.True(sample.MemoryLimitBytes > 0);
                break;
            }

            var content = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024 * 1024);
            await client.PutArchiveAsync(name, "/tmp", async (stream, ct) =>
            {
                await using var writer = new System.Formats.Tar.TarWriter(stream, leaveOpen: true);
                var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "berth-ssh.bin") { DataStream = new MemoryStream(content) };
                await writer.WriteEntryAsync(entry, ct);
            }, cts.Token);
            await using (var archive = await client.GetArchiveAsync(name, "/tmp/berth-ssh.bin", cts.Token))
            {
                await using var reader = new System.Formats.Tar.TarReader(archive.Content);
                var entry = await reader.GetNextEntryAsync(copyData: true, cts.Token);
                using var copy = new MemoryStream();
                await entry!.DataStream!.CopyToAsync(copy, cts.Token);
                Assert.Equal(content, copy.ToArray());
            }

            var pulled = new List<PullProgress>();
            await foreach (var progress in client.PullImageAsync("alpine:3.19", cts.Token))
                pulled.Add(progress);
            Assert.Contains(pulled, p => p.Status.Contains("alpine:3.19", StringComparison.Ordinal));
        }
        finally
        {
            await Cli().RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Streams_past_the_hosts_channel_limit_go_on_another_session()
    {
        await using var host = await SshStandIn.StartAsync(Cli(), maxSessions: 3);
        using var cts = new CancellationTokenSource(Patience);
        await using var transport = await AcceptedAsync(host, host.Profile(), host.Password, cts.Token);

        var streams = new List<Stream>();
        try
        {
            for (var i = 0; i < 7; i++)
                streams.Add(await transport.OpenAsync(cts.Token));
            foreach (var stream in streams)
            {
                await stream.WriteAsync("GET /_ping HTTP/1.1\r\nHost: docker\r\n\r\n"u8.ToArray(), cts.Token);
                Assert.StartsWith("HTTP/1.1 200 OK", await ReadUntilAsync(stream, "\r\n\r\nOK", cts.Token), StringComparison.Ordinal);
            }

            Assert.Equal(3, transport.SessionCount);
        }
        finally
        {
            foreach (var stream in streams)
                await stream.DisposeAsync();
        }

        Assert.Equal(1, transport.SessionCount);
    }

    [EngineFact]
    public async Task A_password_refused_on_a_second_session_is_tried_once_and_ends_the_connection()
    {
        await using var host = await SshStandIn.StartAsync(Cli(), maxSessions: 1);
        using var cts = new CancellationTokenSource(Patience);
        await using var transport = await AcceptedAsync(host, host.Profile(), host.Password, cts.Token);
        await using var first = await transport.OpenAsync(cts.Token);
        await host.ChangePasswordAsync();

        for (var i = 0; i < 6; i++)
        {
            var ex = await Assert.ThrowsAsync<SshTransportException>(async () => await transport.OpenAsync(cts.Token));
            Assert.Equal((SshFailure.SignInRefused, SecretKind.Password), (ex.Problem.Failure, ex.Problem.Secret!.Value));
        }

        var ended = await Assert.ThrowsAsync<SshTransportException>(async () => await first.ReadExactlyAsync(new byte[1], cts.Token));
        Assert.Equal(SshFailure.SignInRefused, ended.Problem.Failure);
        Assert.Equal(1, Count(await host.LogAsync(), "Failed password for berth"));
    }

    [EngineFact]
    public async Task A_stopped_host_ends_the_streams_on_it()
    {
        await using var host = await SshStandIn.StartAsync(Cli());
        using var cts = new CancellationTokenSource(Patience);
        await using var transport = await AcceptedAsync(host, host.Profile(), host.Password, cts.Token);
        await using var client = new EngineClient(EngineHttp.CreateHandler(transport), transport);
        await client.ConnectAsync(cts.Token);

        var events = Task.Run(async () =>
        {
            await foreach (var _ in client.StreamEventsAsync(cts.Token))
            {
            }
        });
        await Task.Delay(500, cts.Token);
        await host.StopAsync();

        var ex = await Record.ExceptionAsync(() => events);
        Assert.False(cts.IsCancellationRequested, "The stream did not end when the host stopped.");
        if (ex is not null)
            Assert.Equal(0, Assert.IsType<EngineException>(ex).StatusCode);
    }

    private static DockerCli Cli() => new(Environment.GetEnvironmentVariable(EngineFactAttribute.Variable)!.Trim());

    /// <summary>A transport for <paramref name="profile"/> with the host's key accepted the way the app does it: asked first, then saved.</summary>
    private static async Task<SshTransport> AcceptedAsync(
        SshStandIn host, ConnectionProfile profile, string? password, CancellationToken ct, string? passphrase = null)
    {
        var key = await ProbeAsync(host, ct);
        var accepted = profile with { Ssh = profile.Ssh!.Accept(key!) };
        var secrets = new FakeSecrets();
        if (password is not null)
            secrets.Set(accepted.Id, SecretKind.Password, password);
        if (passphrase is not null)
            secrets.Set(accepted.Id, SecretKind.Passphrase, passphrase);
        return new SshTransport(accepted, secrets);
    }

    private static async Task<HostKey?> ProbeAsync(SshStandIn host, CancellationToken ct)
    {
        await using var probe = new SshTransport(host.Profile(), new FakeSecrets());
        var ex = await Assert.ThrowsAsync<SshTransportException>(async () => await probe.OpenAsync(ct));
        return ex.Problem.Offered;
    }

    private static async Task PingAsync(IEngineTransport transport, CancellationToken ct)
    {
        await using var stream = await transport.OpenAsync(ct);
        await stream.WriteAsync("GET /_ping HTTP/1.1\r\nHost: docker\r\n\r\n"u8.ToArray(), ct);
        Assert.StartsWith("HTTP/1.1 200 OK", await ReadUntilAsync(stream, "\r\n\r\nOK", ct), StringComparison.Ordinal);
    }

    private static async Task<string> ReadUntilAsync(Stream stream, string end, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (!text.ToString().Contains(end, StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}

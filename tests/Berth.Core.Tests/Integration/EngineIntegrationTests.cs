using System.Text.Json.Nodes;
using Berth.Connections;
using Berth.Engine;
using Berth.Engine.Models;
using Berth.Session;
using Berth.Transport;

namespace Berth.Tests.Integration;

/// <summary>
/// Runs Berth's client and session against the engine at <c>BERTH_ENGINE</c>. Each container a test needs is a throwaway named
/// <c>berth-it-…</c>, made with the docker CLI and removed in <c>finally</c>; nothing else on the engine is touched.
/// </summary>
public sealed class EngineIntegrationTests
{
    /// <summary>A safety net so an engine that stops answering fails the test instead of hanging the run.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [EngineFact]
    public async Task Connect_reports_a_version_at_or_above_1_44()
    {
        await using var engine = TestEngine.FromEnvironment();
        await using var client = engine.CreateClient();
        using var cts = new CancellationTokenSource(Patience);

        var version = await client.ConnectAsync(cts.Token);

        Assert.True(ApiVersion.TryParse(version.ApiVersion, out var reported), $"Unreadable API version \"{version.ApiVersion}\".");
        Assert.True(reported.CompareTo(ApiVersion.Minimum) >= 0, $"The engine reports API {reported}.");
        Assert.NotNull(client.Negotiated);
        Assert.True(client.Negotiated.Value.CompareTo(ApiVersion.Maximum) <= 0);
    }

    [EngineFact]
    public async Task List_contains_a_container_created_by_the_cli()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            var containers = await client.ListContainersAsync(cts.Token);

            var listed = Assert.Single(containers, c => c.Id == id);
            Assert.Equal(["/" + name], listed.Names);
            Assert.Equal("running", listed.State);
            Assert.Equal("alpine:latest", listed.Image);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Start_stop_pause_unpause_restart_and_remove_change_the_engine_state()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            await client.StopContainerAsync(id, cts.Token);
            Assert.Equal("exited", await engine.Cli.StatusAsync(name));

            await client.StartContainerAsync(id, cts.Token);
            Assert.Equal("running", await engine.Cli.StatusAsync(name));

            await client.PauseContainerAsync(id, cts.Token);
            Assert.Equal("paused", await engine.Cli.StatusAsync(name));

            await client.UnpauseContainerAsync(id, cts.Token);
            Assert.Equal("running", await engine.Cli.StatusAsync(name));

            var startedBefore = await engine.Cli.StartedAtAsync(name);
            await client.RestartContainerAsync(id, cts.Token);
            Assert.Equal("running", await engine.Cli.StatusAsync(name));
            Assert.NotEqual(startedBefore, await engine.Cli.StartedAtAsync(name));

            await client.RemoveContainerAsync(id, force: true, cts.Token);
            Assert.False(await engine.Cli.ExistsAsync(id));
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task The_event_stream_reports_a_start()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            // The stream's request goes out before the CLI is asked to create the container.
            var started = FirstStartAsync(client.StreamEventsAsync(cts.Token), name);
            var id = await engine.Cli.RunAsync(name);
            var message = await started.WaitAsync(cts.Token);

            Assert.Equal("container", message.Type);
            Assert.Equal("start", message.Action);
            Assert.Equal(id, message.ActorId);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task A_session_sees_a_cli_started_container_within_one_second()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new TaskCompletionSource<EngineState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new EngineSession(engine.CreateClient, TimeProvider.System);
        session.StateChanged += state =>
        {
            if (state.Status == ConnectionStatus.Connected)
                connected.TrySetResult();
            if (state.Containers.Any(c => c.Names.Contains("/" + name) && c.State == "running"))
                seen.TrySetResult(state);
        };

        try
        {
            session.Dispatch(new ConnectRequested());
            await connected.Task.WaitAsync(Patience);

            await engine.Cli.RunAsync(name);
            var arrived = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(1))) == seen.Task;

            Assert.True(arrived, "No state listing the container arrived within one second of the CLI returning.");
            Assert.Equal(ConnectionStatus.Connected, (await seen.Task).Status);
        }
        finally
        {
            await session.DisposeAsync();
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Inspect_matches_the_cli()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(
                "run", "-d", "--name", name, "--stop-timeout", "1", "--cpus", "0.5", "-w", "/tmp", "--label", "berth.it=inspect",
                "alpine:latest", "sleep", "300");
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            var inspection = await client.InspectContainerAsync(id, cts.Token);

            var details = inspection.Details;
            var cli = (await engine.Cli.RunAsync(
                "inspect", "--format",
                "{{.Id}}|{{.Name}}|{{.Config.Image}}|{{.Image}}|{{.State.Status}}|{{.State.Running}}|{{.Config.Tty}}|{{.Config.WorkingDir}}|{{.HostConfig.RestartPolicy.Name}}|{{.Path}}",
                name)).Split('|');
            Assert.Equal(cli[0], details.Id);
            Assert.Equal(cli[1].TrimStart('/'), details.Name);
            Assert.Equal(cli[2], details.Image);
            Assert.Equal(cli[3], details.ImageId);
            Assert.Equal(cli[4], details.State.Status);
            Assert.Equal(bool.Parse(cli[5]), details.State.Running);
            Assert.Equal(bool.Parse(cli[6]), details.Tty);
            Assert.Equal(cli[7], details.WorkingDir);
            Assert.Equal(cli[8], details.RestartPolicy);
            Assert.Equal(cli[9], details.Path);
            Assert.Equal(["300"], details.Args);
            Assert.Equal("inspect", details.Labels["berth.it"]);
            Assert.Equal(0.5, details.CpuLimit);
            Assert.NotNull(details.State.StartedAt);
            Assert.Contains(id, inspection.RawJson, StringComparison.Ordinal);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Logs_follow_a_container_that_prints()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(
                "run", "-d", "--name", name, "--stop-timeout", "1", "alpine:latest", "sh", "-c", CountingScript("echo line $i"));
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            // Followed from the first line; ten lines take about two seconds to arrive, so most come while following.
            var lines = await FirstLinesAsync(client.StreamLogsAsync(id, tty: false, new LogRequest(Tail: -1, Follow: true), cts.Token), 10);

            Assert.Equal(Enumerable.Range(1, 10).Select(i => $"line {i}"), lines.Select(l => l.Text));
            Assert.All(lines, l => Assert.Equal(LogStream.Stdout, l.Stream));
            Assert.All(lines, l => Assert.NotNull(l.Timestamp));
            var times = lines.Select(l => l.Timestamp!.Value).ToList();
            Assert.Equal(times.Order(), times);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Logs_of_a_tty_container_are_readable()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            // Coloured output through a terminal: the log is plain text with \r\n line ends and ANSI codes, not framed.
            var id = await engine.Cli.RunAsync(
                "run", "-d", "-t", "--name", name, "--stop-timeout", "1", "alpine:latest",
                "sh", "-c", CountingScript(@"printf '\033[32mline %d\033[0m\n' $i"));
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);
            var details = (await client.InspectContainerAsync(id, cts.Token)).Details;

            var lines = await FirstLinesAsync(client.StreamLogsAsync(id, details.Tty, new LogRequest(Tail: -1, Follow: true), cts.Token), 5);

            Assert.True(details.Tty);
            Assert.Equal(Enumerable.Range(1, 5).Select(i => $"line {i}"), lines.Select(l => l.Text));
            Assert.All(lines, l => Assert.NotNull(l.Timestamp));
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Stats_report_cpu_and_memory_for_a_busy_container()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(
                "run", "-d", "--name", name, "--stop-timeout", "1", "--cpus", "0.5", "alpine:latest", "sh", "-c", "while true; do :; done");
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            // The first sample has no previous one to measure CPU against; wait for one that shows the loop running.
            StatsSample? busy = null;
            await foreach (var sample in client.StreamStatsAsync(id, cts.Token))
            {
                if (sample.CpuPercent > 25)
                {
                    busy = sample;
                    break;
                }
            }

            Assert.NotNull(busy);
            Assert.InRange(busy.CpuPercent, 25, 75);
            Assert.True(busy.MemoryBytes > 0, $"Memory {busy.MemoryBytes}");
            Assert.True(busy.MemoryLimitBytes >= busy.MemoryBytes, $"Limit {busy.MemoryLimitBytes}, memory {busy.MemoryBytes}");
            Assert.True(busy.Pids >= 1, $"Pids {busy.Pids}");
            Assert.NotEqual(DateTimeOffset.MinValue, busy.Time);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task A_detail_session_ends_its_streams_when_the_container_stops()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        var errors = new List<string>();
        var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<DetailState>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var id = await engine.Cli.RunAsync(
                "run", "-d", "--name", name, "--stop-timeout", "1", "alpine:latest", "sh", "-c", CountingScript("echo line $i"));
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            await using var session = new ContainerDetailSession(client, id, TimeProvider.System, (what, _) =>
            {
                lock (errors)
                    errors.Add(what);
            });
            session.StateChanged += state =>
            {
                if (state is { LogStatus: StreamStatus.Live, StatsStatus: StreamStatus.Live })
                    live.TrySetResult();
                if (state is { LogStatus: StreamStatus.Ended, StatsStatus: StreamStatus.Ended, Details.State.Running: false })
                    ended.TrySetResult(state);
            };

            session.Open();
            await live.Task.WaitAsync(cts.Token);
            await engine.Cli.RunAsync("stop", name);
            var final = await ended.Task.WaitAsync(cts.Token);

            Assert.Equal("exited", final.Details!.State.Status);
            Assert.Null(final.LogError);
            Assert.Null(final.StatsError);
            Assert.False(final.Removed);
            Assert.False(final.ConnectionLost);
            Assert.NotEmpty(final.Logs.Lines);
            Assert.NotNull(final.Stats.Latest);
            lock (errors)
                Assert.Empty(errors);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Images_volumes_networks_and_disk_usage_are_read()
    {
        await using var engine = TestEngine.FromEnvironment();
        await using var client = engine.CreateClient();
        using var cts = new CancellationTokenSource(Patience);
        await client.ConnectAsync(cts.Token);

        var images = await client.ListImagesAsync(cts.Token);
        var alpine = Assert.Single(images, i => i.RepoTags.Contains("alpine:latest"));
        var details = await client.InspectImageAsync("alpine:latest", cts.Token);
        await client.ListVolumesAsync(cts.Token);
        var networks = await client.ListNetworksAsync(cts.Token);
        var usage = await client.GetDiskUsageAsync(cts.Token);

        Assert.Equal(alpine.Id, details.Id);
        Assert.Equal("linux", details.Os);
        Assert.Contains(networks, n => n.Name == "bridge");
        Assert.Contains(usage.Images, i => i.Id == alpine.Id);
        Assert.Equal(images.Count, usage.ImageTotals.TotalCount);
    }

    [EngineFact]
    public async Task Pulling_an_image_that_is_present_reports_it_up_to_date()
    {
        await using var engine = TestEngine.FromEnvironment();
        await using var client = engine.CreateClient();
        using var cts = new CancellationTokenSource(Patience);
        await client.ConnectAsync(cts.Token);

        var lines = new List<PullProgress>();
        await foreach (var line in client.PullImageAsync("alpine", cts.Token))
            lines.Add(line);

        Assert.Contains("alpine:latest", lines[^1].Status, StringComparison.Ordinal);
    }

    [EngineFact]
    public async Task A_container_created_by_Berth_has_its_env_port_and_volume_and_is_listed_with_them()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        var volume = DockerCli.NewContainerName();
        await using var client = engine.CreateClient();
        using var cts = new CancellationTokenSource(Patience);
        await client.ConnectAsync(cts.Token);
        try
        {
            Assert.Equal(volume, (await client.CreateVolumeAsync(volume, cts.Token)).Name);
            Assert.Contains(await client.ListVolumesAsync(cts.Token), v => v.Name == volume);

            var id = await client.CreateContainerAsync(
                new ContainerCreateRequest("alpine:latest", name, ["BERTH_IT=1"], [new PortBinding(80, "tcp", "127.0.0.1", null)], [$"{volume}:/data"], ["true"]),
                cts.Token);

            var inspection = await client.InspectContainerAsync(id, cts.Token);
            var inspected = inspection.Details;
            Assert.Equal(name, inspected.Name);
            Assert.Contains("BERTH_IT=1", inspected.Env);
            Assert.Equal(["true"], inspected.Cmd);
            Assert.Contains(inspected.Mounts, m => m.Name == volume && m.Destination == "/data");
            // A container that never ran has no ports in NetworkSettings yet: read what was asked for.
            var binding = JsonNode.Parse(inspection.RawJson)!["HostConfig"]!["PortBindings"]!["80/tcp"]![0]!;
            Assert.Equal("127.0.0.1", (string?)binding["HostIp"]);
            Assert.Equal("", (string?)binding["HostPort"]);

            var listed = Assert.Single(await client.ListContainersAsync(cts.Token), c => c.Id == id);
            Assert.Contains(listed.Mounts, m => m.Name == volume);
            Assert.Equal(["bridge"], listed.Networks);

            await client.RemoveContainerAsync(id, force: true, cts.Token);
            await client.RemoveVolumeAsync(volume, force: false, cts.Token);
            Assert.DoesNotContain(await client.ListVolumesAsync(cts.Token), v => v.Name == volume);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
            await engine.Cli.RemoveVolumeAsync(volume);
        }
    }

    /// <summary>A shell loop that runs <paramref name="print"/> with <c>$i</c> counting up from 1, five times a second.</summary>
    private static string CountingScript(string print) => $"i=0; while true; do i=$((i+1)); {print}; sleep 0.2; done";

    /// <summary>The first <paramref name="count"/> lines of a log stream; the stream is left once they are read.</summary>
    private static async Task<List<LogLine>> FirstLinesAsync(IAsyncEnumerable<LogLine> log, int count)
    {
        var lines = new List<LogLine>();
        await foreach (var line in log)
        {
            lines.Add(line);
            if (lines.Count == count)
                return lines;
        }

        throw new InvalidOperationException($"The log ended after {lines.Count} of {count} lines.");
    }

    private static async Task<EngineEventMessage> FirstStartAsync(IAsyncEnumerable<EngineEventMessage> events, string name)
    {
        await foreach (var message in events)
        {
            if (message is { Type: "container", Action: "start" }
                && message.Attributes.TryGetValue("name", out var actor) && actor == name)
            {
                return message;
            }
        }

        throw new InvalidOperationException("The event stream ended before the container started.");
    }

    /// <summary>
    /// The engine at <c>BERTH_ENGINE</c>, reached the way the app reaches it: a profile, its transport, then a client over that transport.
    /// The clients are disposed by their users (a session disposes each one it creates); the transports are disposed here, last.
    /// </summary>
    private sealed class TestEngine : IAsyncDisposable
    {
        private readonly ConnectionProfile _profile;
        private readonly List<IEngineTransport> _transports = [];
        private readonly Lock _gate = new();

        private TestEngine(ConnectionProfile profile, string address)
        {
            _profile = profile;
            Cli = new DockerCli(address);
        }

        public DockerCli Cli { get; }

        public static TestEngine FromEnvironment()
        {
            var address = Environment.GetEnvironmentVariable(EngineFactAttribute.Variable) ?? "";
            if (!EngineAddress.TryParse(address, out var kind, out var host, out var port, out var pipeName, out var error))
                throw new InvalidOperationException($"{EngineFactAttribute.Variable} is not an engine address: {error}");

            var profile = new ConnectionProfile("integration", "Integration", kind, host, port, pipeName, Tls: null);
            return new TestEngine(profile, address.Trim());
        }

        public EngineClient CreateClient()
        {
            var transport = TransportFactory.Create(_profile);
            lock (_gate)
                _transports.Add(transport);
            return new EngineClient(EngineHttp.CreateHandler(transport));
        }

        public async ValueTask DisposeAsync()
        {
            IEngineTransport[] transports;
            lock (_gate)
                transports = [.. _transports];
            foreach (var transport in transports)
                await transport.DisposeAsync();
        }
    }
}

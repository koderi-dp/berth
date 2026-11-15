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

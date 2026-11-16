using System.Text.Json.Nodes;
using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using Berth.State;

namespace Berth.Tests.Integration;

/// <summary>
/// Images, volumes and networks against the engine at <c>BERTH_ENGINE</c>. Only <c>berth-it-…</c> things are made and removed, and
/// <c>alpine:3.19</c> is pulled when it is missing and never removed. Nothing is pruned.
/// </summary>
public sealed partial class EngineIntegrationTests
{
    private const string PulledImage = "alpine:3.19";

    [EngineFact]
    public async Task A_pulled_image_is_listed_and_inspected_and_a_cli_tag_of_it_is_removed_by_the_client()
    {
        await using var engine = TestEngine.FromEnvironment();
        await using var client = engine.CreateClient();
        using var cts = new CancellationTokenSource(Patience);
        await client.ConnectAsync(cts.Token);
        var tag = $"{DockerCli.NewContainerName()}/x:1";
        try
        {
            if (!(await client.ListImagesAsync(cts.Token)).Any(i => i.RepoTags.Contains(PulledImage)))
            {
                var lines = new List<PullProgress>();
                await foreach (var line in client.PullImageAsync(PulledImage, cts.Token))
                    lines.Add(line);
                Assert.Contains(PulledImage, lines[^1].Status, StringComparison.Ordinal);
            }

            var image = Assert.Single(await client.ListImagesAsync(cts.Token), i => i.RepoTags.Contains(PulledImage));
            Assert.Equal(image.Id, (await client.InspectImageAsync(PulledImage, cts.Token)).Details.Id);

            await engine.Cli.RunAsync("tag", PulledImage, tag);
            Assert.Contains(tag, Assert.Single(await client.ListImagesAsync(cts.Token), i => i.Id == image.Id).RepoTags);

            var removed = await client.RemoveImageAsync(tag, cts.Token);

            Assert.Contains(removed, r => r.Untagged == tag);
            Assert.DoesNotContain(removed, r => r.Deleted is not null);
            var after = Assert.Single(await client.ListImagesAsync(cts.Token), i => i.Id == image.Id);
            Assert.DoesNotContain(tag, after.RepoTags);
            Assert.Contains(PulledImage, after.RepoTags);
        }
        finally
        {
            await engine.Cli.RemoveTagAsync(tag);
        }
    }

    [EngineFact]
    public async Task A_volume_is_created_listed_and_removed()
    {
        await using var engine = TestEngine.FromEnvironment();
        await using var client = engine.CreateClient();
        using var cts = new CancellationTokenSource(Patience);
        await client.ConnectAsync(cts.Token);
        var volume = DockerCli.NewContainerName();
        try
        {
            Assert.Equal(volume, (await client.CreateVolumeAsync(volume, cts.Token)).Name);
            Assert.Contains(await client.ListVolumesAsync(cts.Token), v => v.Name == volume);
            Assert.Equal(volume, await engine.Cli.RunAsync("volume", "inspect", "--format", "{{.Name}}", volume));

            await client.RemoveVolumeAsync(volume, cts.Token);

            Assert.DoesNotContain(await client.ListVolumesAsync(cts.Token), v => v.Name == volume);
            Assert.Equal("", await engine.Cli.RunAsync("volume", "ls", "-q", "--filter", $"name={volume}"));
        }
        finally
        {
            await engine.Cli.RemoveVolumeAsync(volume);
        }
    }

    [EngineFact]
    public async Task A_cli_created_network_is_listed_and_removed_by_the_client()
    {
        await using var engine = TestEngine.FromEnvironment();
        await using var client = engine.CreateClient();
        using var cts = new CancellationTokenSource(Patience);
        await client.ConnectAsync(cts.Token);
        var network = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync("network", "create", network);

            var listed = Assert.Single(await client.ListNetworksAsync(cts.Token), n => n.Name == network);
            Assert.Equal(id, listed.Id);
            Assert.Equal("bridge", listed.Driver);

            await client.RemoveNetworkAsync(listed.Id, cts.Token);

            Assert.DoesNotContain(await client.ListNetworksAsync(cts.Token), n => n.Name == network);
            Assert.Equal("", await engine.Cli.RunAsync("network", "ls", "-q", "--filter", $"name={network}"));
        }
        finally
        {
            await engine.Cli.RemoveNetworkAsync(network);
        }
    }

    [EngineFact]
    public async Task A_run_from_a_request_starts_a_container_with_its_port_bind_and_environment_variable()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        var session = new EngineSession(engine.CreateClient, TimeProvider.System);
        try
        {
            session.Dispatch(new ConnectRequested());
            await WaitForAsync(session, s => s.Status == ConnectionStatus.Connected);

            session.Dispatch(new RunContainerRequested(1, new ContainerCreateRequest(
                "alpine:latest", name, ["BERTH_IT=1"], [new PortBinding(80, "tcp", "127.0.0.1", null)], ["/tmp:/host-tmp:ro"], ["sleep", "300"])));
            var state = await WaitForAsync(session, s => s.Run is { Status: not RunStatus.InProgress });

            Assert.Equal(RunStatus.Started, state.Run!.Status);
            Assert.Equal("running", await engine.Cli.StatusAsync(name));
            Assert.StartsWith("127.0.0.1:", await engine.Cli.RunAsync("port", name, "80/tcp"), StringComparison.Ordinal);
            Assert.Contains("BERTH_IT=1", Strings(await engine.Cli.RunAsync("inspect", "--format", "{{json .Config.Env}}", name)));
            Assert.Equal(["/tmp:/host-tmp:ro"], Strings(await engine.Cli.RunAsync("inspect", "--format", "{{json .HostConfig.Binds}}", name)));
            Assert.Equal(state.Run.ContainerId, await engine.Cli.RunAsync("inspect", "--format", "{{.Id}}", name));
        }
        finally
        {
            await session.DisposeAsync();
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task A_session_sees_a_cli_created_volume_and_network_without_a_refresh()
    {
        await using var engine = TestEngine.FromEnvironment();
        var volume = DockerCli.NewContainerName();
        var network = DockerCli.NewContainerName();
        var session = new EngineSession(engine.CreateClient, TimeProvider.System);
        try
        {
            session.Dispatch(new ConnectRequested());
            await WaitForAsync(session, s => s.Status == ConnectionStatus.Connected);
            session.Dispatch(new VolumesWanted());
            session.Dispatch(new NetworksWanted());
            await WaitForAsync(session, s => s.Volumes.Loaded && s.Networks.Loaded);

            await engine.Cli.RunAsync("volume", "create", volume);
            await engine.Cli.RunAsync("network", "create", network);
            var state = await WaitForAsync(session, s => s.Volumes.Items.Any(v => v.Name == volume) && s.Networks.Items.Any(n => n.Name == network));

            Assert.Equal(ConnectionStatus.Connected, state.Status);
        }
        finally
        {
            await session.DisposeAsync();
            await engine.Cli.RemoveVolumeAsync(volume);
            await engine.Cli.RemoveNetworkAsync(network);
        }
    }

    /// <summary>
    /// "Remove unused volumes" through the session: the unused <c>berth-it-</c> volume is named and removed, the one a container mounts is
    /// not named and stays. Only this test's volumes are sent: the plan is narrowed to them before it is confirmed.
    /// </summary>
    [DesktopEngineFact]
    public async Task Removing_unused_volumes_removes_an_unused_one_and_keeps_one_in_use()
    {
        await using var engine = TestEngine.FromEnvironment();
        var unused = DockerCli.NewContainerName();
        var used = DockerCli.NewContainerName();
        var user = DockerCli.NewContainerName();
        var session = new EngineSession(engine.CreateClient, TimeProvider.System);
        try
        {
            await engine.Cli.RunAsync("volume", "create", unused);
            await engine.Cli.RunAsync("volume", "create", used);
            await engine.Cli.RunAsync("create", "--name", user, "-v", $"{used}:/data", "alpine:latest", "true");

            session.Dispatch(new ConnectRequested());
            session.Dispatch(new VolumesWanted());
            var before = await WaitForAsync(session, s =>
                s.Status == ConnectionStatus.Connected && s.ListLoaded && s.Volumes.Items.Any(v => v.Name == unused)
                && s.Containers.Any(c => c.Names.Contains("/" + user)));

            var plan = Confirmations.PruneVolumes(before);
            Assert.Contains(unused, plan.Names);
            Assert.DoesNotContain(used, plan.Names);
            var mine = plan with { Items = [.. plan.Items.Where(i => i.Id == unused)] };

            session.Dispatch(Confirmations.ConfirmPrune(mine, session.State).ToEvent());
            var after = await WaitForAsync(session, s => s.Volumes.Removal is { IsRunning: false } && s.Volumes.Items.All(v => v.Name != unused));

            Assert.Empty(after.Volumes.Removal!.Refusals);
            Assert.Equal("", await engine.Cli.RunAsync("volume", "ls", "-q", "--filter", $"name={unused}"));
            Assert.Equal(used, await engine.Cli.RunAsync("volume", "inspect", "--format", "{{.Name}}", used));
        }
        finally
        {
            await session.DisposeAsync();
            await engine.Cli.RemoveAsync(user);
            await engine.Cli.RemoveVolumeAsync(unused);
            await engine.Cli.RemoveVolumeAsync(used);
        }
    }

    /// <summary>The first state of <paramref name="session"/> (now or later) that <paramref name="done"/> holds for.</summary>
    private static async Task<EngineState> WaitForAsync(EngineSession session, Func<EngineState, bool> done)
    {
        var seen = new TaskCompletionSource<EngineState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(EngineState state)
        {
            if (done(state))
                seen.TrySetResult(state);
        }

        session.StateChanged += OnChanged;
        try
        {
            if (done(session.State))
                return session.State;
            return await seen.Task.WaitAsync(Patience);
        }
        finally
        {
            session.StateChanged -= OnChanged;
        }
    }

    /// <summary>A JSON array of strings the CLI printed (<c>--format "{{json …}}"</c>); <c>null</c> reads as none.</summary>
    private static string[] Strings(string json) => JsonNode.Parse(json) is JsonArray array ? [.. array.Select(n => (string)n!)] : [];
}

using System.Text;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Integration;

/// <summary>
/// Exec against the engine at <c>BERTH_ENGINE</c>, in a throwaway <c>berth-it-…</c> container: create, start over the switched
/// connection, resize, inspect, and the stream ending when the shell exits or Berth closes its side.
/// </summary>
public sealed partial class EngineIntegrationTests
{
    private static readonly TerminalSize ExecSize = new(80, 24);

    [EngineFact]
    public async Task An_exec_prints_its_output_ends_its_stream_and_reports_its_exit_code()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            var execId = await client.CreateExecAsync(id, ["sh", "-c", "echo berth"], ExecSize, [], cts.Token);
            string output;
            await using (var stream = await client.StartExecAsync(execId, ExecSize, cts.Token))
                output = await ReadToEndAsync(stream, cts.Token);

            Assert.Contains("berth", output);
            Assert.Equal(new ExecInspection(Running: false, ExitCode: 0), await EndedAsync(client, execId, cts.Token));
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task A_resized_exec_shell_reports_the_new_size_and_exits_when_told()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            var execId = await client.CreateExecAsync(id, ["sh"], ExecSize, [], cts.Token);
            await using var stream = await client.StartExecAsync(execId, ExecSize, cts.Token);
            await client.ResizeExecAsync(execId, new TerminalSize(120, 30), cts.Token);
            await stream.WriteAsync("stty size; exit 3\n"u8.ToArray(), cts.Token);
            await stream.FlushAsync(cts.Token);

            Assert.Contains("30 120", await ReadToEndAsync(stream, cts.Token));
            Assert.Equal(new ExecInspection(Running: false, ExitCode: 3), await EndedAsync(client, execId, cts.Token));
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Closing_the_write_side_ends_the_exec_stream_where_the_transport_can()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            var execId = await client.CreateExecAsync(id, ["cat"], ExecSize, [], cts.Token);
            await using var stream = await client.StartExecAsync(execId, ExecSize, cts.Token);
            await stream.WriteAsync("berth\n"u8.ToArray(), cts.Token);
            await stream.FlushAsync(cts.Token);
            await ReadUntilAsync(stream, "berth", cts.Token);

            if (!await stream.CloseWriteAsync())
                return; // A named pipe has no half to close; disposing the stream ends the exec's connection instead.

            // The engine reads the end of the input and ends the stream.
            await ReadToEndAsync(stream, cts.Token);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task The_clean_up_ends_a_marked_shell_and_what_it_started_but_not_another_shell()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);
            var marker = Berth.Session.ExecCleanUp.NewMarker();
            var other = Berth.Session.ExecCleanUp.NewMarker();

            // The engine keeps a TTY shell after its connection closes; only the clean-up ends it.
            var marked = await client.CreateExecAsync(id, ["sh"], ExecSize, [Berth.Session.ExecCleanUp.Variable(marker)], cts.Token);
            var spared = await client.CreateExecAsync(id, ["sh"], ExecSize, [Berth.Session.ExecCleanUp.Variable(other)], cts.Token);
            await using var markedStream = await client.StartExecAsync(marked, ExecSize, cts.Token);
            await using var sparedStream = await client.StartExecAsync(spared, ExecSize, cts.Token);
            await markedStream.WriteAsync("trap '' HUP; sleep 600 & echo started\n"u8.ToArray(), cts.Token);
            await markedStream.FlushAsync(cts.Token);
            await ReadUntilAsync(markedStream, "started", cts.Token);

            await client.RunDetachedExecAsync(id, Berth.Session.ExecCleanUp.Command(marker), cts.Token);

            Assert.False((await EndedAsync(client, marked, cts.Token)).Running);
            // The sleep ignored the hang-up; the kill a second later ends it.
            await WaitUntilAsync(async () => !(await engine.Cli.TopAsync(name)).Contains("sleep 600", StringComparison.Ordinal), cts.Token);
            Assert.True((await client.InspectExecAsync(spared, cts.Token)).Running);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> done, CancellationToken ct)
    {
        while (!await done())
            await Task.Delay(100, ct);
    }

    /// <summary>Inspects the exec until it no longer runs (the engine can report the end a moment after the stream closes).</summary>
    private static async Task<ExecInspection> EndedAsync(IEngineApi client, string execId, CancellationToken ct)
    {
        while (true)
        {
            var inspection = await client.InspectExecAsync(execId, ct);
            if (!inspection.Running)
                return inspection;
            await Task.Delay(50, ct);
        }
    }

    private static async Task<string> ReadToEndAsync(Stream stream, CancellationToken ct)
    {
        using var all = new MemoryStream();
        await stream.CopyToAsync(all, ct);
        return Encoding.UTF8.GetString(all.ToArray());
    }

    /// <summary>Reads until the output so far holds <paramref name="text"/>; the stream ending first fails the test.</summary>
    private static async Task ReadUntilAsync(Stream stream, string text, CancellationToken ct)
    {
        var seen = new StringBuilder();
        var buffer = new byte[4096];
        while (!seen.ToString().Contains(text, StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, ct);
            Assert.True(read > 0, $"The stream ended before \"{text}\" arrived.");
            seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }
}

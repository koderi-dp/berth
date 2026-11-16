using System.Text;
using System.Text.RegularExpressions;
using Berth.Engine;
using Berth.Engine.Models;
using Berth.Session;
using Xunit.Abstractions;

namespace Berth.Tests.Integration;

/// <summary>
/// Exec against the engine at <c>BERTH_ENGINE</c>, in a throwaway <c>berth-it-…</c> container: create, start over the switched
/// connection, resize, inspect, and the stream ending when the shell exits or Berth closes its side.
/// </summary>
public sealed partial class EngineIntegrationTests
{
    private static readonly TerminalSize ExecSize = new(80, 24);

    private readonly ITestOutputHelper _output;

    public EngineIntegrationTests(ITestOutputHelper output) => _output = output;

    [EngineFact]
    public async Task Exec_prints_and_reports_its_exit_code()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            var execId = await client.CreateExecAsync(id, ["sh", "-c", "echo berth; exit 3"], ExecSize, [], cts.Token);
            string output;
            await using (var stream = await client.StartExecAsync(execId, ExecSize, cts.Token))
                output = await ReadToEndAsync(stream, cts.Token);

            Assert.Contains("berth", output);
            Assert.Equal(new ExecInspection(Running: false, ExitCode: 3), await EndedAsync(client, execId, cts.Token));
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Exec_is_two_way()
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
            // The echo of the typed line holds "6*7", not "42": only the shell's answer does.
            await WriteAsync(stream, "echo $((6*7))\n", cts.Token);
            await ReadUntilAsync(stream, "42", cts.Token);
            await WriteAsync(stream, "exit\n", cts.Token);
            await ReadToEndAsync(stream, cts.Token);

            Assert.Equal(new ExecInspection(Running: false, ExitCode: 0), await EndedAsync(client, execId, cts.Token));
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Exec_starts_at_the_given_size_and_follows_a_resize()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);
            var size = new TerminalSize(100, 30);

            var execId = await client.CreateExecAsync(id, ["sh"], size, [], cts.Token);
            await using var stream = await client.StartExecAsync(execId, size, cts.Token);
            await WriteAsync(stream, "stty size\n", cts.Token);
            await ReadUntilAsync(stream, "30 100", cts.Token);

            await client.ResizeExecAsync(execId, new TerminalSize(120, 40), cts.Token);
            await WriteAsync(stream, "stty size; exit\n", cts.Token);

            Assert.Contains("40 120", await ReadToEndAsync(stream, cts.Token));
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Exec_sets_a_terminal_type()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            var execId = await client.CreateExecAsync(id, ["sh", "-c", "echo \"TERM=[$TERM]\""], ExecSize, [], cts.Token);
            string output;
            await using (var stream = await client.StartExecAsync(execId, ExecSize, cts.Token))
                output = await ReadToEndAsync(stream, cts.Token);

            var term = Regex.Match(output, @"TERM=\[(?<value>[^\]]*)\]").Groups["value"].Value;
            _output.WriteLine($"TERM in a Berth exec: \"{term}\"");
            Assert.NotEmpty(term);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task A_missing_shell_says_so_in_the_stream_and_exits_126_or_127()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            var execId = await client.CreateExecAsync(id, ["no-such-shell"], ExecSize, [], cts.Token);
            string output;
            await using (var stream = await client.StartExecAsync(execId, ExecSize, cts.Token))
                output = await ReadToEndAsync(stream, cts.Token);

            _output.WriteLine($"The engine's words: {output.Trim()}");
            Assert.Contains("no-such-shell", output, StringComparison.Ordinal);
            var ended = await EndedAsync(client, execId, cts.Token);
            Assert.Contains(ended.ExitCode, new int?[] { 126, 127 });
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Exec_in_a_stopped_container_is_the_engines_409()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await engine.Cli.RunAsync("stop", name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            var refused = await Assert.ThrowsAsync<EngineException>(() => client.CreateExecAsync(id, ["sh"], ExecSize, [], cts.Token));

            _output.WriteLine($"The engine's words: {refused.Message}");
            Assert.Equal(409, refused.StatusCode);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Exec_in_a_paused_container_is_the_engines_409()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await engine.Cli.RunAsync("pause", name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            var refused = await Assert.ThrowsAsync<EngineException>(() => client.CreateExecAsync(id, ["sh"], ExecSize, [], cts.Token));

            _output.WriteLine($"The engine's words: {refused.Message}");
            Assert.Equal(409, refused.StatusCode);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task A_megabyte_goes_through_unchanged()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);
            var sent = new byte[1024 * 1024];
            new Random(4).NextBytes(sent);
            // The one byte the engine holds back: the first key of Berth's detach sequence (EngineClient.ExecDetachKeys). Any byte
            // that starts the sequence waits for the next, so random bytes would end the stream only if they held it whole, but a
            // held byte at the very end would never come back.
            for (var i = 0; i < sent.Length; i++)
            {
                if (sent[i] == DetachPrefix)
                    sent[i] = 0x20;
            }

            // Raw and without echo, the terminal hands cat every byte as it is and cat writes it back as it is.
            var execId = await client.CreateExecAsync(id, ["sh", "-c", "stty raw -echo; echo ready; exec cat"], ExecSize, [], cts.Token);
            await using var stream = await client.StartExecAsync(execId, ExecSize, cts.Token);
            await ReadUntilAsync(stream, "ready\n", cts.Token);

            var writing = Task.Run(async () =>
            {
                for (var at = 0; at < sent.Length; at += 16 * 1024)
                {
                    await stream.WriteAsync(sent.AsMemory(at, Math.Min(16 * 1024, sent.Length - at)), cts.Token);
                    await stream.FlushAsync(cts.Token);
                }
            });
            var received = new byte[sent.Length];
            var got = 0;
            while (got < received.Length)
            {
                var read = await stream.ReadAsync(received.AsMemory(got), cts.Token);
                Assert.True(read > 0, $"The stream ended after {got} of {sent.Length} bytes.");
                got += read;
            }

            await writing;
            var first = sent.AsSpan().CommonPrefixLength(received);
            Assert.True(first == sent.Length, $"The bytes differ first at {first} of {sent.Length}.");
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task Ctrl_p_reaches_the_shell_at_once_and_ctrl_p_ctrl_q_does_not_detach()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            var execId = await client.CreateExecAsync(id, ["sh", "-c", "stty raw -echo; echo ready; exec cat"], ExecSize, [], cts.Token);
            await using var stream = await client.StartExecAsync(execId, ExecSize, cts.Token);
            await ReadUntilAsync(stream, "ready\n", cts.Token);

            // Alone, with nothing after it: the engine's default detach keys would hold it back until the next key.
            await stream.WriteAsync(new byte[] { 0x10 }, cts.Token);
            await stream.FlushAsync(cts.Token);
            Assert.Equal([0x10], await ReadExactlyAsync(stream, 1, TimeSpan.FromSeconds(5)));

            // The engine's default detach sequence: it goes to the shell and the stream stays open.
            await stream.WriteAsync(new byte[] { 0x10, 0x11, (byte)'x' }, cts.Token);
            await stream.FlushAsync(cts.Token);
            Assert.Equal([0x10, 0x11, (byte)'x'], await ReadExactlyAsync(stream, 3, TimeSpan.FromSeconds(5)));
            Assert.True((await client.InspectExecAsync(execId, cts.Token)).Running);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task A_detail_session_runs_a_shell_and_stops_it()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        var errors = new List<string>();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CollectingOutput();
        try
        {
            var id = await engine.Cli.RunAsync(name);
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
                if (state.Details is { State.Running: true })
                    loaded.TrySetResult();
                if (state.Exec.Status == ExecStatus.Running)
                    running.TrySetResult(state.Exec.Generation);
                if (running.Task.IsCompleted && state.Exec.Status == ExecStatus.NotStarted)
                    stopped.TrySetResult();
            };
            session.AttachExecOutput(sink);
            session.Open();
            await loaded.Task.WaitAsync(cts.Token);

            session.StartExec(["sh"], ExecSize);
            var generation = await running.Task.WaitAsync(cts.Token);
            // The typed line echoes "o''k"; only the shell's answer is a line that is "ok".
            Assert.Equal(ExecInputOutcome.Sent, session.SendExecInput(generation, "echo o''k\n"u8.ToArray()));
            await sink.WaitForLineAsync("ok", cts.Token);

            session.StopExec();
            await stopped.Task.WaitAsync(cts.Token);
            var after = sink.Count;
            // A shell left alone prints nothing; what a stopped one still sent would have arrived within this time.
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);

            Assert.Equal(ExecStatus.NotStarted, session.State.Exec.Status);
            Assert.Equal(after, sink.Count);
            lock (errors)
                Assert.Empty(errors);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task What_a_closed_shell_leaves_behind()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);

            // No marker and no clean-up: what the engine itself does with a TTY shell whose connection closes (risk R7).
            var execId = await client.CreateExecAsync(id, ["sh"], ExecSize, [], cts.Token);
            await using (var stream = await client.StartExecAsync(execId, ExecSize, cts.Token))
            {
                await WriteAsync(stream, "sleep 300 & echo started-$((1+1))\n", cts.Token);
                await ReadUntilAsync(stream, "started-2", cts.Token);
                await WriteAsync(stream, "sleep 301\n", cts.Token);
                await WaitUntilAsync(async () => (await engine.Cli.TopAsync(name)).Contains("sleep 301", StringComparison.Ordinal), cts.Token);
                _output.WriteLine($"Write side closed: {await stream.CloseWriteAsync()}");
            }

            // Nothing to wait for: the question is what is still there a moment after the close.
            await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
            _output.WriteLine($"The exec two seconds after its stream closed: {await client.InspectExecAsync(execId, cts.Token)}");
            _output.WriteLine("docker top at the same time:");
            _output.WriteLine(await engine.Cli.TopAsync(name));
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
    public async Task The_clean_up_hangs_up_a_marked_shell_and_what_it_started_but_spares_nohup_and_another_shell()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);
            var marker = ExecCleanUp.NewMarker();
            var other = ExecCleanUp.NewMarker();

            // The engine keeps a TTY shell after its connection closes; only the clean-up ends it.
            var marked = await client.CreateExecAsync(id, ["sh"], ExecSize, [ExecCleanUp.Variable(marker)], cts.Token);
            var spared = await client.CreateExecAsync(id, ["sh"], ExecSize, [ExecCleanUp.Variable(other)], cts.Token);
            await using var markedStream = await client.StartExecAsync(marked, ExecSize, cts.Token);
            await using var sparedStream = await client.StartExecAsync(spared, ExecSize, cts.Token);
            await markedStream.WriteAsync("nohup sleep 600 >/dev/null 2>&1 & sleep 601 & top\n"u8.ToArray(), cts.Token);
            await markedStream.FlushAsync(cts.Token);
            await WaitUntilAsync(async () => (await engine.Cli.TopAsync(name)) is var top && top.Contains("top", StringComparison.Ordinal)
                && top.Contains("sleep 600", StringComparison.Ordinal) && top.Contains("sleep 601", StringComparison.Ordinal), cts.Token);

            await client.RunDetachedExecAsync(id, ExecCleanUp.Command(marker), cts.Token);

            Assert.False((await EndedAsync(client, marked, cts.Token)).Running);
            // The shell, top and the plain background job end with the hang-up; the nohup job ignores it and stays.
            await WaitUntilAsync(async () => !(await engine.Cli.TopAsync(name)).Contains("sleep 601", StringComparison.Ordinal), cts.Token);
            var left = await engine.Cli.TopAsync(name);
            Assert.DoesNotContain(left.Split('\n'), line => line.TrimEnd().EndsWith(" top", StringComparison.Ordinal));
            Assert.Contains("sleep 600", left, StringComparison.Ordinal);
            Assert.True((await client.InspectExecAsync(spared, cts.Token)).Running);
        }
        finally
        {
            // The nohup job goes with the container.
            await engine.Cli.RemoveAsync(name);
        }
    }

    /// <summary>Ctrl+^, the first key of <see cref="EngineClient.ExecDetachKeys"/>.</summary>
    private const byte DetachPrefix = 0x1e;

    /// <summary>Reads exactly <paramref name="count"/> bytes, failing when they do not all arrive within <paramref name="within"/>.</summary>
    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, TimeSpan within)
    {
        using var limit = new CancellationTokenSource(within);
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, limit.Token);
        return bytes;
    }

    private static async Task WriteAsync(Stream stream, string text, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>A terminal that keeps what it is given and says when a whole line has arrived.</summary>
    private sealed class CollectingOutput : IExecOutput
    {
        private readonly Lock _lock = new();
        private readonly StringBuilder _text = new();
        private int _count;

        public int Count
        {
            get
            {
                lock (_lock)
                    return _count;
            }
        }

        public ValueTask WriteAsync(int generation, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            lock (_lock)
            {
                _count += bytes.Length;
                _text.Append(Encoding.UTF8.GetString(bytes.Span));
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>Waits, polling what arrived, until a line that is exactly <paramref name="line"/> has.</summary>
        public async Task WaitForLineAsync(string line, CancellationToken ct)
        {
            var pattern = new Regex($@"(^|\n){Regex.Escape(line)}\r?\n");
            while (true)
            {
                lock (_lock)
                {
                    if (pattern.IsMatch(_text.ToString()))
                        return;
                }

                await Task.Delay(50, ct);
            }
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

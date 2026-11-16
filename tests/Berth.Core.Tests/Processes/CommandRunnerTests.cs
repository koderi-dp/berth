using System.ComponentModel;
using System.Diagnostics;
using Berth.Processes;

namespace Berth.Tests.Processes;

/// <summary>Runs real programs that every Windows machine has (cmd.exe, ping.exe); nothing touches an engine.</summary>
public sealed class CommandRunnerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Gives_the_exit_code_and_the_output()
    {
        var outcome = await CommandRunner.RunAsync("cmd.exe", ["/c", "echo engine said no& echo to stderr 1>&2& exit /b 3"], CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal("engine said no\nto stderr", outcome.Output);
    }

    [Fact]
    public async Task A_quiet_success_has_no_output()
    {
        var outcome = await CommandRunner.RunAsync("cmd.exe", ["/c", "exit /b 0"], CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("", outcome.Output);
    }

    [Fact]
    public async Task Arguments_reach_the_program_as_given()
    {
        var outcome = await CommandRunner.RunAsync("cmd.exe", ["/c", "echo", "a b", @"C:\dir\"], CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("\"a b\" C:\\dir\\", outcome.Output);
    }

    [Fact]
    public async Task A_program_that_cannot_be_found_throws()
    {
        await Assert.ThrowsAsync<Win32Exception>(() => CommandRunner.RunAsync("berth-no-such-program.exe", [], CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_last_lines_are_kept()
    {
        var outcome = await CommandRunner.RunAsync("cmd.exe", ["/c", "for /l %i in (1,1,50) do @echo line %i"], CancellationToken.None).WaitAsync(Patience);

        var lines = outcome.Output.Split('\n');
        Assert.Equal(CommandRunner.OutputLines, lines.Length);
        Assert.Equal("line 50", lines[^1]);
    }

    [Fact]
    public async Task Ends_when_the_command_exits_even_if_a_program_it_started_keeps_its_output_open()
    {
        var started = DateTime.Now;
        try
        {
            // A start command that launches something long-lived (a stand-in engine) and returns: the child inherits the output pipe.
            var outcome = await CommandRunner.RunAsync("cmd.exe", ["/c", "start /b ping -n 30 127.0.0.1& echo started"], CancellationToken.None).WaitAsync(Patience);

            Assert.Equal(0, outcome.ExitCode);
            Assert.StartsWith("started", outcome.Output);
        }
        finally
        {
            KillPingsStartedAfter(started);
        }
    }

    [Fact]
    public async Task Cancelling_ends_the_whole_process_tree()
    {
        var started = DateTime.Now;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => CommandRunner.RunAsync("cmd.exe", ["/c", "ping -n 60 127.0.0.1"], cancel.Token).WaitAsync(Patience));

            // Ended processes can take a moment to show as exited.
            for (var i = 0; i < 50 && PingsStartedAfter(started).Length > 0; i++)
                await Task.Delay(100);
            Assert.Empty(PingsStartedAfter(started));
        }
        finally
        {
            KillPingsStartedAfter(started);
        }
    }

    private static Process[] PingsStartedAfter(DateTime time) =>
        [.. Process.GetProcessesByName("PING").Where(p =>
        {
            try
            {
                return !p.HasExited && p.StartTime >= time.AddSeconds(-1);
            }
            catch (Exception)
            {
                return false;
            }
        })];

    private static void KillPingsStartedAfter(DateTime time)
    {
        foreach (var ping in PingsStartedAfter(time))
        {
            try
            {
                ping.Kill();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }
    }
}

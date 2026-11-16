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
    public async Task Runs_in_the_user_profile_folder_whatever_folder_berth_was_started_from()
    {
        var outcome = await CommandRunner.RunAsync("cmd.exe", ["/c", "cd"], CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), outcome.Output, ignoreCase: true);
    }

    [Fact]
    public async Task Ends_when_the_command_exits_even_if_a_program_it_started_keeps_its_output_open()
    {
        using var child = new ChildPid();
        try
        {
            // A start command that launches something long-lived (a stand-in engine) and returns: the child shares the console and
            // its output handles. The child's process id goes to a file this test owns.
            var outcome = await CommandRunner.RunAsync(
                    "powershell.exe",
                    ["-NoProfile", "-Command", $"$p = Start-Process ping.exe -ArgumentList '-n','30','127.0.0.1' -NoNewWindow -PassThru; Set-Content -LiteralPath '{child.File}' -Value $p.Id; 'started'"],
                    CancellationToken.None)
                .WaitAsync(Patience);

            Assert.Equal(0, outcome.ExitCode);
            // The ping shares the output, so its lines may come before or after.
            Assert.Contains("started", outcome.Output.Split('\n'));
            Assert.True(child.IsRunning(), "the started program should still run");
        }
        finally
        {
            child.EndIfRunning();
        }
    }

    [Fact]
    public async Task Cancelling_ends_the_whole_process_tree()
    {
        using var child = new ChildPid();
        using var cancel = new CancellationTokenSource();
        try
        {
            var run = CommandRunner.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-Command", $"$p = Start-Process ping.exe -ArgumentList '-n','60','127.0.0.1' -NoNewWindow -PassThru; Set-Content -LiteralPath '{child.File}' -Value $p.Id; Wait-Process -Id $p.Id"],
                cancel.Token);
            await child.WaitForPid().WaitAsync(Patience);
            Assert.True(child.IsRunning());

            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Patience));

            // An ended process can take a moment to show as exited.
            for (var i = 0; i < 50 && child.IsRunning(); i++)
                await Task.Delay(100);
            Assert.False(child.IsRunning(), "the grandchild should have been ended with the tree");
        }
        finally
        {
            child.EndIfRunning();
        }
    }

    /// <summary>
    /// The process id of the one program a test's command starts, written by the command to a file the test owns. Only that process
    /// is ever looked at or ended, and only while it is still the ping.exe started after the test began.
    /// </summary>
    private sealed class ChildPid : IDisposable
    {
        private readonly DateTime _since = DateTime.Now.AddSeconds(-1);

        public string File { get; } = Path.Combine(Path.GetTempPath(), "berth-test-" + Guid.NewGuid().ToString("N") + ".pid");

        public async Task WaitForPid()
        {
            while (Read() is null)
                await Task.Delay(50);
        }

        public bool IsRunning() => Owned() is { } process && !process.HasExited;

        public void EndIfRunning()
        {
            try
            {
                if (Owned() is { HasExited: false } process)
                    process.Kill();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        public void Dispose()
        {
            try
            {
                System.IO.File.Delete(File);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless.
            }
        }

        private int? Read()
        {
            try
            {
                return int.TryParse(System.IO.File.ReadAllText(File).Trim(), out var pid) ? pid : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private Process? Owned()
        {
            if (Read() is not { } pid)
                return null;
            try
            {
                var process = Process.GetProcessById(pid);
                return process.ProcessName.Equals("PING", StringComparison.OrdinalIgnoreCase) && process.StartTime >= _since ? process : null;
            }
            catch (ArgumentException)
            {
                return null; // exited
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
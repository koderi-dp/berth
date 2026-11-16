using System.Diagnostics;

namespace Berth.Processes;

/// <summary>How a command ended: its exit code and the last lines it wrote (standard output, then standard error), as it wrote them.</summary>
public sealed record CommandOutcome(int ExitCode, string Output);

/// <summary>
/// Runs a program on this computer as the user, with no shell (nothing in the arguments is expanded, redirected or chained) and no
/// window, and captures the end of what it writes. Used for a connection's engine control commands. The output may hold anything the
/// program printed: callers never log it.
/// </summary>
public static class CommandRunner
{
    /// <summary>How many lines of output are kept.</summary>
    public const int OutputLines = 20;

    /// <summary>How many bytes of each stream are kept while the program runs.</summary>
    public const int OutputBytes = 64 * 1024;

    /// <summary>How long the output is still read after the program exited (a program it started may hold the pipe open).</summary>
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Runs <paramref name="program"/> with <paramref name="arguments"/> and returns when it exits, even when a program it started keeps
    /// running. Throws what <see cref="Process.Start()"/> throws when it cannot start (a <see cref="System.ComponentModel.Win32Exception"/>
    /// for a program not found). When <paramref name="cancellationToken"/> is cancelled the program and every process it started are
    /// ended, and <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    public static async Task<CommandOutcome> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        process.Start();
        // Nothing is typed into it: a program that asks for input reads the end of it instead of waiting.
        process.StandardInput.Close();

        var output = new OutputTail(OutputBytes);
        var errors = new OutputTail(OutputBytes);
        var reading = Task.WhenAll(
            PumpAsync(process.StandardOutput.BaseStream, output),
            PumpAsync(process.StandardError.BaseStream, errors));

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EndTree(process);
            throw;
        }

        try
        {
            await reading.WaitAsync(OutputGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A program the command started still holds the pipe: what arrived so far is the output.
        }

        var text = ConsoleText.Decode(output.ToArray()).TrimEnd() + "\n" + ConsoleText.Decode(errors.ToArray());
        return new CommandOutcome(process.ExitCode, ConsoleText.LastLines(text.Trim('\n', '\r'), OutputLines));
    }

    private static void EndTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // It exited by itself meanwhile, or a process in the tree could not be ended: nothing more can be done.
        }
    }

    private static async Task PumpAsync(Stream stream, OutputTail tail)
    {
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                tail.Append(buffer.AsSpan(0, read));
        }
        catch (Exception)
        {
            // The pipe was closed under the read (the process disposed): what was read is kept.
        }
    }
}

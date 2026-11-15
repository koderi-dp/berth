using System.ComponentModel;
using System.Diagnostics;

namespace Berth.Tests.Integration;

/// <summary>
/// Runs the docker CLI against one engine, independently of Berth's client. <c>DOCKER_HOST</c> is set for the child process only, and
/// <c>DOCKER_CONTEXT</c> is removed, so the caller's environment does not decide which engine is used.
/// </summary>
internal sealed class DockerCli(string engineAddress)
{
    /// <summary>Long enough for a container start or stop; a hung CLI fails the test instead of blocking the run.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>A name for a throwaway container; only names with this prefix may be created or removed by the tests.</summary>
    public static string NewContainerName() => $"berth-it-{Guid.NewGuid():N}";

    /// <summary>Starts a small container that stays up for five minutes and stops at once when asked. Returns its full id.</summary>
    public Task<string> RunAsync(string name) =>
        RunAsync("run", "-d", "--name", name, "--stop-timeout", "1", "alpine:latest", "sleep", "300");

    /// <summary>The container's <c>State.Status</c>, such as "running", "paused" or "exited".</summary>
    public Task<string> StatusAsync(string name) => RunAsync("inspect", "--format", "{{.State.Status}}", name);

    public Task<string> StartedAtAsync(string name) => RunAsync("inspect", "--format", "{{.State.StartedAt}}", name);

    /// <summary>True when a container with this id, running or not, exists.</summary>
    public async Task<bool> ExistsAsync(string id) => (await RunAsync("ps", "-a", "-q", "--no-trunc", "--filter", $"id={id}")).Length > 0;

    /// <summary>Removes the container if it exists. Used in <c>finally</c>, so a missing container is not an error.</summary>
    public async Task RemoveAsync(string name)
    {
        var (exitCode, _, error) = await ExecuteAsync(["rm", "-f", name]);
        if (exitCode != 0 && !error.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"docker rm -f {name} failed ({exitCode}): {error}");
    }

    /// <summary>Runs the CLI and returns its trimmed output; a non-zero exit throws with what the CLI wrote to stderr.</summary>
    public async Task<string> RunAsync(params string[] arguments)
    {
        var (exitCode, output, error) = await ExecuteAsync(arguments);
        if (exitCode != 0)
            throw new InvalidOperationException($"docker {string.Join(' ', arguments)} failed ({exitCode}): {error}");
        return output;
    }

    private async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(string[] arguments)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["DOCKER_HOST"] = engineAddress;
        start.Environment.Remove("DOCKER_CONTEXT");

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("The docker CLI did not start.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("The integration tests need the docker CLI on PATH; it could not be started: " + ex.Message, ex);
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(Limit);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"docker {string.Join(' ', arguments)} did not finish within {Limit.TotalSeconds} seconds.");
            }

            return (process.ExitCode, (await output).Trim(), (await error).Trim());
        }
    }
}

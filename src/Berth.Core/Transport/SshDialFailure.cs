using Berth.Connections;

namespace Berth.Transport;

/// <summary>What a <c>docker system dial-stdio</c> that ended with an error means, from its exit status and what it wrote to stderr.</summary>
public static class SshDialFailure
{
    /// <summary>
    /// The shell's "command not found" (exit 127) is a host without the docker CLI; the engine's socket refusing the user is a user who
    /// may not use docker; anything else is dial-stdio's own failure. The stderr goes with it as-is (bounded by <see cref="SshProblem"/>).
    /// </summary>
    public static SshProblem Classify(int? exitStatus, string stderr)
    {
        var text = stderr.Trim();
        if (exitStatus == 127 || (text.Contains("docker", StringComparison.OrdinalIgnoreCase) && text.Contains("not found", StringComparison.OrdinalIgnoreCase)))
            return new SshProblem(SshFailure.NoDockerCli, text);
        if (text.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
            return new SshProblem(SshFailure.DockerNotPermitted, text);
        return new SshProblem(SshFailure.DialFailed, text.Length > 0 ? text : $"It ended with exit status {exitStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}.");
    }
}

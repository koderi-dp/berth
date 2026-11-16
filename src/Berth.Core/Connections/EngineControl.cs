namespace Berth.Connections;

/// <summary>
/// How Berth starts and stops a connection's engine: two command lines as the user typed them (<see cref="CommandLine"/>), run on this
/// computer as the user, with no shell.
/// </summary>
public sealed record EngineControl(string StartCommand, string StopCommand)
{
    /// <summary>
    /// The commands for docker-ce run by systemd in the WSL distro <paramref name="distro"/>, as root so no password is asked. Stopping
    /// names <c>docker.socket</c> as well: the engine is socket-activated (<c>docker.service</c> requires <c>docker.socket</c>, which
    /// listens on <c>/run/docker.sock</c>), and with the socket left listening the next client in the distro would start it again.
    /// Starting <c>docker</c> starts its socket too.
    /// </summary>
    public static EngineControl WslDefault(string distro) => new(
        CommandLine.Format("wsl.exe", ["-d", distro, "-u", "root", "--", "systemctl", "start", "docker"]),
        CommandLine.Format("wsl.exe", ["-d", distro, "-u", "root", "--", "systemctl", "stop", "docker.socket", "docker"]));
}

namespace Berth.Engine.Models;

/// <summary>The parts of <c>/info</c> Berth shows.</summary>
public sealed record EngineInfo(
    string Name,
    string ServerVersion,
    int Cpus,
    long MemoryBytes,
    int ContainersRunning,
    int ContainersPaused,
    int ContainersStopped,
    int Images)
{
    /// <summary>The engine's operating system as it names it ("Ubuntu 24.04.5 LTS", "Docker Desktop"); empty when it gave none.</summary>
    public string OperatingSystem { get; init; } = "";

    /// <summary>The kernel the engine runs on ("6.18.58.1-microsoft-standard-WSL2"); empty when it gave none.</summary>
    public string KernelVersion { get; init; } = "";
}

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
    int Images);

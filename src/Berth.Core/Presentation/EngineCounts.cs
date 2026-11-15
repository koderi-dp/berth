using Berth.Engine.Models;

namespace Berth.Presentation;

/// <summary>The container counts the status bar shows.</summary>
public static class EngineCounts
{
    /// <summary>"{n} running · {p} paused · {m} stopped"; the paused part is left out when nothing is paused.</summary>
    public static string Text(EngineInfo info) =>
        info.ContainersPaused > 0
            ? $"{info.ContainersRunning} running · {info.ContainersPaused} paused · {info.ContainersStopped} stopped"
            : $"{info.ContainersRunning} running · {info.ContainersStopped} stopped";
}

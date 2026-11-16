using Berth.State;

namespace Berth.Presentation;

/// <summary>What the run dialog says about its run.</summary>
public static class RunOutcome
{
    /// <summary>
    /// The line for <paramref name="run"/>: progress while it runs, nothing once started (the app opens the container), the engine's
    /// message as it is when the create failed or after it when the start failed, and for an interrupted run whether the container may
    /// exist, and for one not sent that Berth is not connected.
    /// </summary>
    public static string? Text(ContainerRun run) => run.Status switch
    {
        RunStatus.InProgress => "Creating and starting the container…",
        RunStatus.Started => null,
        RunStatus.Failed => run.Error,
        RunStatus.StartFailed => $"The container was created but could not be started: {run.Error}",
        RunStatus.NotConnected => VolumeRows.NotConnectedText,
        _ => run.ContainerId is null
            ? "The connection to the engine changed. The container may have been created; look for it in the Containers list."
            : "The connection to the engine changed after the container was created and before it started.",
    };
}

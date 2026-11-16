namespace Berth.State;

/// <summary>
/// Where running a container from an image is: creating and starting it, started, failed before a container existed, created but
/// not started, or interrupted because the connection to the engine changed (the container may or may not exist: see
/// <see cref="ContainerRun.ContainerId"/>).
/// </summary>
public enum RunStatus { InProgress, Started, Failed, StartFailed, Interrupted }

/// <summary>
/// The container the run dialog asked for, until the dialog dismisses it.
/// </summary>
/// <param name="Image">The image it runs.</param>
/// <param name="ContainerId">
/// The new container's ID once created: set when <see cref="Status"/> is <see cref="RunStatus.Started"/> (the app opens its page)
/// or <see cref="RunStatus.StartFailed"/> (the container exists, stopped, and the dialog can link to it), and as soon as the create
/// went through while the run is in progress or was interrupted.
/// </param>
/// <param name="Error">The engine's message when the create or the start failed.</param>
public sealed record ContainerRun(string Image, RunStatus Status, string? ContainerId, string? Error);

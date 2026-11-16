namespace Berth.State;

/// <summary>
/// Where running a container from an image is: creating and starting it, started, failed before a container existed, created but
/// not started, interrupted because the connection to the engine changed (the container may or may not exist: see
/// <see cref="ContainerRun.ContainerId"/>), or not sent because the engine was not connected.
/// </summary>
public enum RunStatus { InProgress, Started, Failed, StartFailed, Interrupted, NotConnected }

/// <summary>
/// The container the run dialog asked for, by its request, until the dialog dismisses it. A request is always answered here: the dialog
/// reads only the entry with its own <see cref="RequestId"/>.
/// </summary>
/// <param name="RequestId">The dialog's number for this run; a later request replaces the entry, and results of an earlier one are dropped.</param>
/// <param name="Image">The image it runs.</param>
/// <param name="ContainerId">
/// The new container's ID once created: set when <see cref="Status"/> is <see cref="RunStatus.Started"/> (the app opens its page)
/// or <see cref="RunStatus.StartFailed"/> (the container exists, stopped, and the dialog can link to it), and as soon as the create
/// went through while the run is in progress or was interrupted.
/// </param>
/// <param name="Error">The engine's message when the create or the start failed.</param>
public sealed record ContainerRun(int RequestId, string Image, RunStatus Status, string? ContainerId, string? Error);

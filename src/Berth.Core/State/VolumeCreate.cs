namespace Berth.State;

/// <summary>
/// Where the create volume dialog's create is: under way, created, refused by the engine, interrupted by a change of connection (the
/// volume may or may not exist), or not sent because the engine was not connected or that name was already being created or removed.
/// </summary>
public enum VolumeCreateStatus { InProgress, Created, Refused, Interrupted, NotConnected, NameBusy }

/// <summary>
/// The create the dialog asked for, by its request, until the dialog dismisses it. A request is always answered here: the dialog reads
/// only the entry with its own <see cref="RequestId"/>.
/// </summary>
/// <param name="RequestId">The dialog's number for this create; a later request replaces the entry.</param>
/// <param name="Name">The name asked for.</param>
/// <param name="Error">The engine's message when it refused the create, as it is.</param>
public sealed record VolumeCreate(int RequestId, string Name, VolumeCreateStatus Status, string? Error = null)
{
    /// <summary>The create has ended (created, refused, interrupted or not sent).</summary>
    public bool HasEnded => Status != VolumeCreateStatus.InProgress;
}

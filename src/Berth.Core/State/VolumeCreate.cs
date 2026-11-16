namespace Berth.State;

/// <summary>Where the create volume dialog's create is: under way, created, or refused by the engine.</summary>
public enum VolumeCreateStatus { InProgress, Created, Refused }

/// <summary>
/// The volume the create dialog asked for, until the dialog dismisses it. Kept for one connection: a change of connection drops it.
/// </summary>
/// <param name="Name">The name asked for.</param>
/// <param name="Error">The engine's message when it refused the create, as it is.</param>
/// <param name="Unattended">
/// The dialog closed while the create was under way: no dialog shows how it ends, so a refusal goes to the app's error bar instead and the
/// outcome is dropped when the create ends.
/// </param>
public sealed record VolumeCreate(string Name, VolumeCreateStatus Status, string? Error, bool Unattended = false);

using Berth.Engine.Models;

namespace Berth.State;

/// <summary>
/// The one rule for "in use": a container in any state uses an image it was created from (or the engine counts one), a volume it mounts
/// as a volume, and a network it is attached to (by name). The session checks it before sending a "Remove unused" item, and the lists,
/// filters and confirmations show it.
/// </summary>
public static class InUse
{
    /// <summary>A mount of a named or anonymous volume, not a bind or tmpfs.</summary>
    public static bool IsVolumeMount(MountDetails mount) => mount.Type == "volume" && !string.IsNullOrEmpty(mount.Name);

    /// <summary><paramref name="container"/> mounts the volume <paramref name="volume"/>.</summary>
    public static bool Mounts(ContainerSummary container, string volume) => container.Mounts.Any(m => IsVolumeMount(m) && m.Name == volume);

    /// <summary><paramref name="container"/> is attached to the network named <paramref name="network"/>.</summary>
    public static bool JoinsNetwork(ContainerSummary container, string network) => container.Networks.Contains(network, StringComparer.Ordinal);

    /// <summary>The engine counts a container of <paramref name="image"/>, or one of <paramref name="containers"/> was created from it.</summary>
    public static bool Image(ImageSummary image, IEnumerable<ContainerSummary> containers) =>
        (image.Containers ?? 0) > 0 || containers.Any(c => c.ImageId == image.Id);

    public static bool Volume(string volume, IEnumerable<ContainerSummary> containers) => containers.Any(c => Mounts(c, volume));

    public static bool Network(string network, IEnumerable<ContainerSummary> containers) => containers.Any(c => JoinsNetwork(c, network));
}

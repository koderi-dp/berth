namespace Berth.Presentation;

/// <summary>The page a container's page was opened from: the Containers list, a volume's page, the Networks list or the Logs page.</summary>
public enum BackPlace { Containers, Volume, Networks, Logs }

/// <summary>Where a container's page goes back to, which is where it was opened from, and what its back button says.</summary>
/// <param name="VolumeName">The volume whose page it was opened from, for <see cref="BackPlace.Volume"/>.</param>
public sealed record BackTarget(BackPlace Place, string? VolumeName = null)
{
    public static readonly BackTarget Containers = new(BackPlace.Containers);

    public static readonly BackTarget Networks = new(BackPlace.Networks);

    public static readonly BackTarget Logs = new(BackPlace.Logs);

    public static BackTarget Volume(string name) => new(BackPlace.Volume, name);

    /// <summary>The back button's name: where it goes ("Back to volume data", "Back to networks", "Back to containers").</summary>
    public string Label => Place switch
    {
        BackPlace.Volume => $"Back to volume {VolumeName}",
        BackPlace.Networks => "Back to networks",
        BackPlace.Logs => "Back to logs",
        _ => "Back to containers",
    };
}

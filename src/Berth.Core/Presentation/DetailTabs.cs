namespace Berth.Presentation;

/// <summary>A tab of a container's page.</summary>
public enum DetailTab { Logs, Inspect, BindMounts, Exec, Files, Stats }

/// <summary>Which tabs a container's page shows, in Docker Desktop's order, and what they are called.</summary>
public static class DetailTabs
{
    /// <summary>Logs, Inspect, Bind mounts, Exec, Files, Stats. One list, so a strip built from it can be kept.</summary>
    public static IReadOnlyList<DetailTab> Shown { get; } =
        [DetailTab.Logs, DetailTab.Inspect, DetailTab.BindMounts, DetailTab.Exec, DetailTab.Files, DetailTab.Stats];

    /// <summary>The tab's title on the strip.</summary>
    public static string Title(DetailTab tab) => tab switch
    {
        DetailTab.Inspect => "Inspect",
        DetailTab.BindMounts => "Bind mounts",
        DetailTab.Exec => "Exec",
        DetailTab.Files => "Files",
        DetailTab.Stats => "Stats",
        _ => "Logs",
    };

    /// <summary>The tab at <paramref name="index"/> of the strip; Logs when the index is out of range.</summary>
    public static DetailTab At(IReadOnlyList<DetailTab> shown, int index) =>
        index >= 0 && index < shown.Count ? shown[index] : DetailTab.Logs;

    /// <summary>Where <paramref name="tab"/> is on the strip; 0 (the first tab) when it is not shown.</summary>
    public static int IndexOf(IReadOnlyList<DetailTab> shown, DetailTab tab)
    {
        for (var i = 0; i < shown.Count; i++)
        {
            if (shown[i] == tab)
                return i;
        }

        return 0;
    }
}

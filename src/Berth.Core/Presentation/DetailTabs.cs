namespace Berth.Presentation;

/// <summary>A tab of a container's page.</summary>
public enum DetailTab { Logs, Inspect, BindMounts, Exec, Stats }

/// <summary>Which tabs a container's page shows, in Docker Desktop's order, and what they are called.</summary>
public static class DetailTabs
{
    private static readonly IReadOnlyList<DetailTab> WithoutExec =
        [DetailTab.Logs, DetailTab.Inspect, DetailTab.BindMounts, DetailTab.Stats];

    private static readonly IReadOnlyList<DetailTab> WithExec =
        [DetailTab.Logs, DetailTab.Inspect, DetailTab.BindMounts, DetailTab.Exec, DetailTab.Stats];

    /// <summary>Logs, Inspect, Bind mounts, Exec when <paramref name="exec"/>, Stats. The same list each time, so a strip built from it can be kept.</summary>
    public static IReadOnlyList<DetailTab> Shown(bool exec) => exec ? WithExec : WithoutExec;

    /// <summary>The tab's title on the strip.</summary>
    public static string Title(DetailTab tab) => tab switch
    {
        DetailTab.Inspect => "Inspect",
        DetailTab.BindMounts => "Bind mounts",
        DetailTab.Exec => "Exec",
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

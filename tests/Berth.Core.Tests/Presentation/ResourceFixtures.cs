using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Session;
using Berth.State;

namespace Berth.Tests.Presentation;

/// <summary>Builds images, volumes, networks and the engine state holding them for the presentation tests.</summary>
internal static class ResourceFixtures
{
    public const string AnonymousName = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static ImageSummary Image(string id, string[]? tags = null, string[]? digests = null, long created = 1_700_000_000, long size = 1000, int? containers = 0) =>
        new(id, tags ?? [], digests ?? [], created, size, containers);

    public static VolumeSummary Volume(string name, string driver = "local", DateTimeOffset? created = null, bool anonymous = false)
    {
        var labels = anonymous ? new Dictionary<string, string> { ["com.docker.volume.anonymous"] = "" } : new Dictionary<string, string>();
        return new VolumeSummary(name, driver, $"/var/lib/docker/volumes/{name}/_data", created, labels, "local");
    }

    public static NetworkSummary Network(string id, string? name = null, string driver = "bridge", string scope = "local", DateTimeOffset? created = null) =>
        new(id, name ?? id, driver, scope, created, false, new Dictionary<string, string>(), []);

    /// <summary>A container that uses image <paramref name="imageId"/>, mounts <paramref name="volumes"/> and joins <paramref name="networks"/>.</summary>
    public static ContainerSummary User(
        string id, string imageId = "sha256:other", string[]? volumes = null, string[]? networks = null, string state = "exited", string? bind = null) =>
        new(id, [$"/{id}"], "img", imageId, "sh", 1_700_000_000, state, "Exited (0)", [], new Dictionary<string, string>())
        {
            Mounts =
            [
                .. (volumes ?? []).Select(v => new MountDetails("volume", $"/var/lib/docker/volumes/{v}/_data", $"/data/{v}", "", ReadWrite: v != "ro", v)),
                .. bind is null ? (MountDetails[])[] : [new MountDetails("bind", bind, "/host", "", true, null)],
            ],
            Networks = networks ?? [],
        };

    public static EngineState StateWith(
        IEnumerable<ImageSummary>? images = null,
        IEnumerable<VolumeSummary>? volumes = null,
        IEnumerable<NetworkSummary>? networks = null,
        IEnumerable<ContainerSummary>? containers = null,
        DiskUsage? usage = null,
        bool connected = true) =>
        EngineState.Initial with
        {
            Status = connected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
            Containers = [.. containers ?? []],
            ListLoaded = connected,
            Images = new ResourceList<ImageSummary>([.. images ?? []], ListStatus.Initial with { Wanted = true, Loaded = true }),
            Volumes = new ResourceList<VolumeSummary>([.. volumes ?? []], ListStatus.Initial with { Wanted = true, Loaded = true }),
            Networks = new ResourceList<NetworkSummary>([.. networks ?? []], ListStatus.Initial with { Wanted = true, Loaded = true }),
            DiskUsage = DiskUsageState.Initial with { Usage = usage, Asked = usage is not null },
        };

    /// <summary><paramref name="state"/> with <paramref name="key"/> pending in the list of <paramref name="kind"/>.</summary>
    public static EngineState Pending(EngineState state, ResourceKind kind, string key, ItemAction action = ItemAction.Remove) => kind switch
    {
        ResourceKind.Images => state with { Images = state.Images with { Status = state.Images.Status with { Pending = state.Images.Pending.SetItem(key, action) } } },
        ResourceKind.Volumes => state with { Volumes = state.Volumes with { Status = state.Volumes.Status with { Pending = state.Volumes.Pending.SetItem(key, action) } } },
        _ => state with { Networks = state.Networks with { Status = state.Networks.Status with { Pending = state.Networks.Pending.SetItem(key, action) } } },
    };

    public static DiskUsage Usage(
        IEnumerable<ImageDiskUsage>? images = null, long imageReclaimable = 0, IEnumerable<VolumeDiskUsage>? volumes = null, long volumeReclaimable = 0) =>
        new([.. images ?? []], new UsageTotals(0, 0, 0, imageReclaimable), [.. volumes ?? []], new UsageTotals(0, 0, 0, volumeReclaimable));

    public static HashSet<string> Keys(params string[] keys) => [.. keys];

    public static string[] Sent(params string[] keys) => keys;
}

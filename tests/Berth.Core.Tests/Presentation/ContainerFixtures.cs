using Berth.Engine.Models;
using Berth.Presentation;

namespace Berth.Tests.Presentation;

/// <summary>Builds container list entries for the presentation tests.</summary>
internal static class ContainerFixtures
{
    public static readonly ContainerFilter NoFilter = new("", RunningOnly: false);

    public static ContainerSummary Container(
        string id,
        string? name = null,
        string state = "running",
        string? project = null,
        string image = "nginx:latest",
        IReadOnlyList<string>? names = null,
        IReadOnlyList<PortMapping>? ports = null,
        long created = 1_700_000_000)
    {
        var labels = new Dictionary<string, string>();
        if (project is not null)
        {
            labels[ComposeGrouping.ProjectLabel] = project;
        }

        return new ContainerSummary(
            id,
            names ?? [$"/{name ?? id}"],
            image,
            "sha256:1",
            "nginx",
            created,
            state,
            "Up 1 minute",
            ports ?? [],
            labels);
    }
}

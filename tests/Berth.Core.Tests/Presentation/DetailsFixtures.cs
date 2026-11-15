using Berth.Engine.Models;

namespace Berth.Tests.Presentation;

/// <summary>Builds inspect details, log lines and stats samples for the presentation tests.</summary>
internal static class DetailsFixtures
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A running container with nothing optional set: no ports, mounts, env, labels, networks or command.</summary>
    public static ContainerDetails Details(string name = "web") => new(
        "abc123",
        name,
        T0,
        "",
        [],
        new ContainerStateDetails("running", true, false, false, 0, "", T0, null, null),
        "sha256:1111",
        "nginx:alpine",
        [],
        [],
        [],
        "",
        "",
        false,
        new Dictionary<string, string>(),
        [],
        [],
        [],
        "no",
        "");

    public static LogLine Line(string text, DateTimeOffset? time = null, LogStream stream = LogStream.Stdout) =>
        new(stream, time, text);

    public static IReadOnlyList<LogLine> Lines(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => Line($"line {i}"))];

    public static StatsSample Sample(
        double seconds,
        double cpu = 0,
        long memory = 0,
        long received = 0,
        long sent = 0,
        long read = 0,
        long written = 0) =>
        new(T0.AddSeconds(seconds), cpu, memory, 1_000_000, received, sent, read, written, 1);
}

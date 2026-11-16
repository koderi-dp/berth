using System.Globalization;
using Berth.Engine.Models;

namespace Berth.Presentation;

/// <summary>One name and value of an inspect section.</summary>
public sealed record InspectRow(string Name, string Value);

/// <summary>A titled table of the Inspect tab.</summary>
public sealed record InspectSection(string Title, IReadOnlyList<InspectRow> Rows);

/// <summary>The tables of the Inspect tab and the list of the Bind mounts tab.</summary>
public static class InspectSections
{
    /// <summary>
    /// The sections Platform, Cmd, State, Image, Ports, Runtime, Mounts, Env, Labels and Networks, in that order; a section with
    /// no rows is left out. Times are in UTC, flags read "Yes" or "No".
    /// </summary>
    public static IReadOnlyList<InspectSection> Build(ContainerDetails details)
    {
        InspectSection[] sections =
        [
            new("Platform", Rows(("Platform", details.Platform))),
            new("Cmd", Rows(("Entrypoint", string.Join(' ', details.Entrypoint)), ("Cmd", string.Join(' ', details.Cmd)))),
            new("State", State(details.State)),
            new("Image", Rows(("Image", details.Image), ("Image ID", details.ImageId))),
            new("Ports", Ports(details.Ports)),
            new("Runtime", Runtime(details)),
            new("Mounts", [.. details.Mounts.Select(Mount)]),
            new("Env", [.. details.Env.Select(EnvRow)]),
            new("Labels", [.. details.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new InspectRow(l.Key, l.Value))]),
            new("Networks",
            [
                .. details.Networks
                    .OrderBy(n => n.Name, StringComparer.Ordinal)
                    .Select(n => new InspectRow(n.Name, n.IpAddress.Length > 0 ? n.IpAddress : "No address")),
            ]),
        ];

        return [.. sections.Where(s => s.Rows.Count > 0)];
    }

    /// <summary>The container's mounts of type "bind", in the engine's order.</summary>
    public static IReadOnlyList<MountDetails> BindMounts(ContainerDetails details) =>
        [.. details.Mounts.Where(m => m.Type == "bind")];

    /// <summary>"Read-only" or "Read-write": what the container may do with a mount.</summary>
    public static string Access(MountDetails mount) => mount.ReadWrite ? "Read-write" : "Read-only";

    /// <summary>
    /// Where a bind mount's host path is on this Windows machine: a Windows path or share as it is (with backslashes), and a Windows
    /// drive as a Linux engine sees it (<c>/mnt/c/...</c> in WSL, <c>/run/desktop/mnt/host/c/...</c> in Docker Desktop) as
    /// <c>C:\...</c>. Null for a path inside the engine's own file system, which Explorer cannot open by that name.
    /// </summary>
    public static string? WindowsPath(string source)
    {
        if (source.Length >= 3 && char.IsAsciiLetter(source[0]) && source[1] == ':' && source[2] is '\\' or '/')
            return char.ToUpperInvariant(source[0]) + source[1..].Replace('/', '\\');
        if (source.StartsWith(@"\\", StringComparison.Ordinal))
            return source;

        foreach (var prefix in (string[])["/mnt/", "/run/desktop/mnt/host/"])
        {
            if (!source.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var rest = source[prefix.Length..];
            if (rest.Length == 0 || !char.IsAsciiLetter(rest[0]) || (rest.Length > 1 && rest[1] != '/'))
                return null;
            return $"{char.ToUpperInvariant(rest[0])}:\\{rest[Math.Min(2, rest.Length)..].Replace('/', '\\')}";
        }

        return null;
    }

    private static List<InspectRow> State(ContainerStateDetails state)
    {
        var rows = new List<InspectRow>
        {
            new("Status", state.Status),
            new("Running", YesNo(state.Running)),
            new("Paused", YesNo(state.Paused)),
            new("Restarting", YesNo(state.Restarting)),
            new("Exit code", state.ExitCode.ToString(CultureInfo.InvariantCulture)),
        };
        rows.AddRange(Rows(
            ("Error", state.Error),
            ("Started", Time(state.StartedAt)),
            ("Finished", Time(state.FinishedAt)),
            ("Health", state.Health)));
        return rows;
    }

    private static List<InspectRow> Ports(IReadOnlyList<PortMapping> ports) =>
    [
        .. ports
            .GroupBy(p => (p.PrivatePort, p.Type))
            .OrderBy(g => g.Key.PrivatePort)
            .ThenBy(g => g.Key.Type, StringComparer.Ordinal)
            .Select(g =>
            {
                var bindings = g.Where(p => p.PublicPort is not null).Select(Binding).Distinct().ToList();
                var value = bindings.Count > 0 ? string.Join(", ", bindings) : "Not published";
                return new InspectRow($"{g.Key.PrivatePort}/{g.Key.Type}", value);
            }),
    ];

    private static string Binding(PortMapping port)
    {
        var ip = port.Ip ?? "";
        var host = ip.Contains(':', StringComparison.Ordinal) ? $"[{ip}]" : ip;
        return host.Length > 0 ? $"{host}:{port.PublicPort}" : $"{port.PublicPort}";
    }

    private static List<InspectRow> Runtime(ContainerDetails details)
    {
        var rows = Rows(("Created", Time(details.Created)), ("Working directory", details.WorkingDir), ("User", details.User));
        rows.Add(new InspectRow("TTY", YesNo(details.Tty)));
        rows.AddRange(Rows(("Restart policy", details.RestartPolicy)));
        return rows;
    }

    /// <summary>"source (type, access)"; a mount without a source, such as a tmpfs, reads "type, access".</summary>
    private static InspectRow Mount(MountDetails mount)
    {
        var source = mount.Name ?? mount.Source;
        var kind = $"{mount.Type}, {(mount.ReadWrite ? "read-write" : "read-only")}";
        return new InspectRow(mount.Destination, source.Length > 0 ? $"{source} ({kind})" : kind);
    }

    private static InspectRow EnvRow(string entry)
    {
        var equals = entry.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? new InspectRow(entry, "") : new InspectRow(entry[..equals], entry[(equals + 1)..]);
    }

    /// <summary>A row for each pair whose value is neither null nor empty.</summary>
    private static List<InspectRow> Rows(params (string Name, string? Value)[] pairs) =>
        [.. pairs.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => new InspectRow(p.Name, p.Value!))];

    private static string? Time(DateTimeOffset? time) =>
        time?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string YesNo(bool value) => value ? "Yes" : "No";
}

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

    private static InspectRow Mount(MountDetails mount) =>
        new(mount.Destination, $"{mount.Name ?? mount.Source} ({mount.Type}, {(mount.ReadWrite ? "read-write" : "read-only")})");

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

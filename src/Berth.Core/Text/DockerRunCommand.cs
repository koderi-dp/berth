using System.Net;
using Berth.Engine.Models;
using static Berth.Text.ShellQuote;

namespace Berth.Text;

/// <summary>The <c>docker run</c> command that creates a container like an existing one.</summary>
public static class DockerRunCommand
{
    /// <summary>
    /// "docker run -d --name N", then "-p" per published port (once per port; with its address when bound to one address, and
    /// "/udp" for a protocol other than TCP), "-v source:destination" per mount (a volume by its name, ":ro" when read-only;
    /// "--tmpfs destination" for a tmpfs), "-e" per environment entry, "--restart" unless the policy is "no", "-w", "-u",
    /// "-t" for a TTY, the image and the command. Each value is written for <paramref name="shell"/> so that pasting the
    /// command passes it through unchanged (<see cref="ShellQuote.Quote"/>).
    /// </summary>
    public static string Build(ContainerDetails details, ShellKind shell)
    {
        var args = new List<string> { "docker", "run", "-d", "--name", Quote(details.Name, shell) };

        var ports = details.Ports
            .Where(p => p.PublicPort is not null)
            .OrderBy(p => p.PublicPort)
            .ThenBy(p => p.PrivatePort)
            .Select(Publish)
            .Distinct();
        foreach (var port in ports)
        {
            args.Add("-p");
            args.Add(Quote(port, shell));
        }

        foreach (var mount in details.Mounts)
        {
            if (mount.Type == "tmpfs")
            {
                // A tmpfs mount has no source, so "-v" cannot express it.
                args.Add("--tmpfs");
                args.Add(Quote(mount.Destination, shell));
                continue;
            }

            args.Add("-v");
            args.Add(Quote($"{mount.Name ?? mount.Source}:{mount.Destination}{(mount.ReadWrite ? "" : ":ro")}", shell));
        }

        foreach (var entry in details.Env)
        {
            args.Add("-e");
            args.Add(Quote(entry, shell));
        }

        if (details.RestartPolicy is not ("" or "no"))
        {
            args.Add("--restart");
            args.Add(Quote(details.RestartPolicy, shell));
        }

        if (details.WorkingDir.Length > 0)
        {
            args.Add("-w");
            args.Add(Quote(details.WorkingDir, shell));
        }

        if (details.User.Length > 0)
        {
            args.Add("-u");
            args.Add(Quote(details.User, shell));
        }

        if (details.Tty)
        {
            args.Add("-t");
        }

        args.Add(Quote(details.Image, shell));
        args.AddRange(details.Cmd.Select(a => Quote(a, shell)));
        return string.Join(' ', args);
    }

    private static string Publish(PortMapping port)
    {
        var protocol = string.Equals(port.Type, "tcp", StringComparison.OrdinalIgnoreCase) ? "" : $"/{port.Type}";
        var mapping = $"{port.PublicPort}:{port.PrivatePort}{protocol}";
        if (string.IsNullOrEmpty(port.Ip) || !IPAddress.TryParse(port.Ip, out var address)
            || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return mapping;
        }

        var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{port.Ip}]" : port.Ip;
        return $"{host}:{mapping}";
    }
}

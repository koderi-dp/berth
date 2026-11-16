using System.Net;
using System.Text;
using Berth.Engine.Models;

namespace Berth.Engine;

/// <summary>The <c>docker run</c> command that creates a container like an existing one.</summary>
public static class DockerRunCommand
{
    /// <summary>
    /// "docker run -d --name N", then "-p" per published port (once per port; with its address when bound to one address, and
    /// "/udp" for a protocol other than TCP), "-v source:destination" per mount (a volume by its name, ":ro" when read-only;
    /// "--tmpfs destination" for a tmpfs), "-e" per environment entry, "--restart" unless the policy is "no", "-w", "-u",
    /// "-t" for a TTY, the image and the command. Each value is written for <paramref name="shell"/> so that pasting the
    /// command passes it through unchanged: bare when it holds only letters, digits and <c>_ - . / : = @ % +</c> (for
    /// PowerShell, and does not start with <c>@</c> or <c>-</c>), otherwise in single quotes.
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

    /// <summary>
    /// A value (every argument but docker's own option names) bare when it is made only of safe characters and, for PowerShell,
    /// does not start with "@", which would splat, or "-", which PowerShell can take for a parameter ("--%" stops its parsing,
    /// "-a.b" is split in two); otherwise in single quotes, inside which neither shell acts on anything but the quote itself.
    /// </summary>
    private static string Quote(string arg, ShellKind shell)
    {
        if (arg.Length > 0 && arg.All(IsSafe) && !(shell == ShellKind.PowerShell && arg[0] is '@' or '-'))
        {
            return arg;
        }

        var text = new StringBuilder("'");
        foreach (var c in arg)
        {
            if (shell == ShellKind.Posix && c == '\'')
            {
                text.Append(@"'\''");
            }
            else if (shell == ShellKind.PowerShell && IsPowerShellSingleQuote(c))
            {
                // PowerShell ends a single-quoted string at any of its single-quote characters; doubling one keeps it.
                text.Append(c).Append(c);
            }
            else
            {
                text.Append(c);
            }
        }

        return text.Append('\'').ToString();
    }

    private static bool IsSafe(char c) => char.IsAsciiLetterOrDigit(c) || "_-./:=@%+".Contains(c, StringComparison.Ordinal);

    private static bool IsPowerShellSingleQuote(char c) => c is '\'' or '‘' or '’' or '‚' or '‛';
}

/// <summary>The shell a copied command is written for.</summary>
public enum ShellKind
{
    /// <summary>PowerShell: a single quote inside single quotes is doubled.</summary>
    PowerShell,

    /// <summary>A POSIX shell such as bash or zsh: a single quote inside single quotes is written <c>'\''</c>.</summary>
    Posix,
}

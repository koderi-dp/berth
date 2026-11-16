using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Berth.Engine.Models;

namespace Berth.Presentation;

/// <summary>A port the image exposes and the host port typed for it: blank publishes nothing, 0 lets the engine pick one.</summary>
public sealed record PortField(int ContainerPort, string Protocol, string HostPort);

/// <summary>A volume row: a host path (or a volume name) and the path inside the container.</summary>
public sealed record VolumeField(string HostPath, string ContainerPath);

/// <summary>An environment row: a variable and its value.</summary>
public sealed record EnvField(string Name, string Value);

/// <summary>What is wrong with each field of a <see cref="RunDraft"/>: null where nothing is; one entry per port, volume and environment row.</summary>
public sealed record RunDraftErrors(string? Name, ImmutableArray<string?> Ports, ImmutableArray<string?> Volumes, ImmutableArray<string?> Env)
{
    /// <summary>No field has an error, so Run is enabled.</summary>
    public bool IsValid => Name is null && Ports.All(e => e is null) && Volumes.All(e => e is null) && Env.All(e => e is null);
}

/// <summary>
/// The run dialog's fields for one image: the container's name, a host port for each port the image exposes, volume rows and
/// environment rows. Rows left blank are ignored. A Windows host path is passed as typed (see <see cref="HostPathNote"/>).
/// </summary>
public sealed partial record RunDraft(string Image, string Name, ImmutableArray<PortField> Ports, ImmutableArray<VolumeField> Volumes, ImmutableArray<EnvField> Env)
{
    /// <summary>What the dialog says under the volumes: an engine in WSL refuses a Windows path ("C:\...").</summary>
    public const string HostPathNote = "For an engine in WSL, write a Windows folder as /mnt/c/... rather than C:\\...";

    public const string NameError = "Use letters, digits, _, . and -, starting with a letter or digit.";
    public const string PortError = "Enter a port from 1 to 65535, or 0 for any free port.";
    public const string VolumeBothError = "Enter both paths, or neither.";
    public const string ContainerPathError = "The container path must start with /.";
    public const string EnvNameMissingError = "Enter the variable's name.";
    public const string EnvNameEqualsError = "A name cannot contain =.";

    /// <summary>
    /// A blank draft for <paramref name="image"/> with one host port field per port it exposes, as the engine names them ("80/tcp"; a
    /// port without a protocol is tcp); a port that cannot be read is left out.
    /// </summary>
    public static RunDraft For(string image, IEnumerable<string> exposedPorts)
    {
        var ports = ImmutableArray.CreateBuilder<PortField>();
        foreach (var exposed in exposedPorts)
        {
            var slash = exposed.IndexOf('/');
            var number = slash < 0 ? exposed : exposed[..slash];
            var protocol = slash < 0 ? "tcp" : exposed[(slash + 1)..];
            if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 && protocol.Length > 0)
                ports.Add(new PortField(port, protocol, ""));
        }

        return new RunDraft(image, "", ports.ToImmutable(), [], []);
    }

    /// <summary>The host ports the running containers publish, which a new container cannot publish again.</summary>
    public static IReadOnlySet<int> PublishedPorts(IEnumerable<ContainerSummary> containers) =>
        containers.Where(c => c.State == "running").SelectMany(c => c.Ports).Select(p => p.PublicPort).OfType<int>().ToHashSet();

    public RunDraft WithHostPort(int index, string hostPort) => this with { Ports = Ports.SetItem(index, Ports[index] with { HostPort = hostPort }) };

    public RunDraft AddVolume() => this with { Volumes = Volumes.Add(new VolumeField("", "")) };

    public RunDraft WithVolume(int index, VolumeField volume) => this with { Volumes = Volumes.SetItem(index, volume) };

    public RunDraft RemoveVolume(int index) => this with { Volumes = Volumes.RemoveAt(index) };

    public RunDraft AddEnv() => this with { Env = Env.Add(new EnvField("", "")) };

    public RunDraft WithEnv(int index, EnvField env) => this with { Env = Env.SetItem(index, env) };

    public RunDraft RemoveEnv(int index) => this with { Env = Env.RemoveAt(index) };

    /// <summary>
    /// What is wrong with each field. The name may be blank (the engine picks one). A host port is blank, 0, or 1 to 65535, not one an
    /// earlier field already took and not one of <paramref name="publishedPorts"/> (<see cref="PublishedPorts"/>). A volume row has both
    /// paths or neither, and the container path starts with "/". An environment row with a value has a name, a name has no "=", and no
    /// name is set twice; the second and later rows of a name carry the error.
    /// </summary>
    public RunDraftErrors Validate(IReadOnlySet<int>? publishedPorts = null)
    {
        var name = Name.Trim();
        var nameError = name.Length == 0 || ValidName().IsMatch(name) ? null : NameError;

        var taken = new HashSet<(int, string)>();
        var ports = Ports.Select(field => PortErrorOf(field, taken, publishedPorts)).ToImmutableArray();

        var volumes = Volumes.Select(v =>
        {
            var host = v.HostPath.Trim();
            var container = v.ContainerPath.Trim();
            return host.Length == 0 && container.Length == 0 ? null
                : host.Length == 0 || container.Length == 0 ? VolumeBothError
                : !container.StartsWith('/') ? ContainerPathError
                : null;
        }).ToImmutableArray();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var env = Env.Select(e =>
        {
            var variable = e.Name.Trim();
            if (variable.Length == 0)
                return e.Value.Length == 0 ? null : EnvNameMissingError;
            if (variable.Contains('='))
                return EnvNameEqualsError;
            return seen.Add(variable) ? null : $"{variable} is set more than once.";
        }).ToImmutableArray();

        return new RunDraftErrors(nameError, ports, volumes, env);
    }

    /// <summary>The create request: the name (null when blank), the ports with a host port, the filled volume and environment rows.</summary>
    /// <exception cref="InvalidOperationException">The draft has an error (<see cref="Validate"/>).</exception>
    public ContainerCreateRequest ToRequest()
    {
        if (!Validate().IsValid)
            throw new InvalidOperationException("The run dialog has fields with errors.");

        var name = Name.Trim();
        return new ContainerCreateRequest(
            Image,
            name.Length == 0 ? null : name,
            [.. Env.Where(e => e.Name.Trim().Length > 0).Select(e => $"{e.Name.Trim()}={e.Value}")],
            [
                .. Ports
                    .Where(p => p.HostPort.Trim().Length > 0)
                    .Select(p => (Field: p, Host: int.Parse(p.HostPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture)))
                    .Select(p => new PortBinding(p.Field.ContainerPort, p.Field.Protocol, null, p.Host == 0 ? null : p.Host)),
            ],
            [.. Volumes.Where(v => v.HostPath.Trim().Length > 0).Select(v => $"{v.HostPath.Trim()}:{v.ContainerPath.Trim()}")]);
    }

    private static string? PortErrorOf(PortField field, HashSet<(int, string)> taken, IReadOnlySet<int>? publishedPorts)
    {
        var text = field.HostPort.Trim();
        if (text.Length == 0)
            return null;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535)
            return PortError;
        if (port == 0)
            return null;
        if (!taken.Add((port, field.Protocol)))
            return $"Port {port} is used by another field.";
        return publishedPorts?.Contains(port) == true ? $"Port {port} is already published by another container." : null;
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex ValidName();
}

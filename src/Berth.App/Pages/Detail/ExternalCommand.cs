using System.Collections.Immutable;
using Berth.Connections;
using Berth.Text;

namespace Berth.Pages.Detail;

/// <summary>Whether <c>docker.exe</c> and <c>wt.exe</c> are on <c>PATH</c>.</summary>
internal sealed record ExternalTools(bool Docker, bool WindowsTerminal)
{
    /// <summary>Looks both up in this process's <c>PATH</c>; touches the disk, so it runs off the UI thread.</summary>
    public static ExternalTools Find()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        return new(
            ExternalTerminal.FindOnPath("docker.exe", path, File.Exists) is not null,
            ExternalTerminal.FindOnPath("wt.exe", path, File.Exists) is not null);
    }
}

/// <summary>
/// The external terminal's command for one shell: the text "Copy command" copies (null when there is none for the connection), what
/// "Open in external terminal" starts (null until the tools were looked up), and why it is not offered.
/// </summary>
internal sealed record ExternalCommand(string? CopyText, ExternalLaunchPlan? Plan, string? Refusal)
{
    public static ExternalCommand For(ConnectionProfile connection, string containerId, string name, ImmutableArray<string> command, ExternalTools? tools)
    {
        if (DockerExecCommand.Unavailable(connection) is { } why)
            return new(null, null, why);

        var arguments = DockerExecCommand.Arguments(connection, containerId, command);
        var text = DockerExecCommand.Text(connection, containerId, command, ShellKind.PowerShell);
        var plan = tools is null ? null : ExternalTerminal.Plan(name.Length > 0 ? name : containerId, arguments, tools.Docker, tools.WindowsTerminal);
        return new(text, plan, plan?.Refusal);
    }
}

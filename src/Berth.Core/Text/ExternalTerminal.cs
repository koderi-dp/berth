using System.Collections.Immutable;

namespace Berth.Text;

/// <summary>
/// A program to start: its file, its arguments one by one (for <c>ProcessStartInfo.ArgumentList</c>), how, and the environment
/// variables taken out of what it inherits (<see cref="ClearedVariables"/>).
/// </summary>
public sealed record ProcessLaunch(string FileName, ImmutableArray<string> Arguments, bool UseShellExecute)
{
    /// <summary>Removed from the started program's environment, so they cannot change what its command means.</summary>
    public ImmutableArray<string> ClearedVariables { get; init; } = [];
}

/// <summary>
/// What "Open in external terminal" starts: the program to try first, the one to try when the first is not installed, or why
/// nothing is started (a sentence the tab shows; "Copy command" still works then).
/// </summary>
public sealed record ExternalLaunchPlan(ProcessLaunch? First, ProcessLaunch? Fallback, string? Refusal);

/// <summary>
/// How "Open in external terminal" starts docker: in a Windows Terminal tab, or, without Windows Terminal, in a console window of
/// its own. Decided here as data; the app starts it.
/// </summary>
/// <remarks>
/// On the way to docker an argument passes Windows Terminal's parsing, which Berth cannot ask to leave it alone (read in its
/// source, microsoft/terminal: <c>AppCommandlineArgs.cpp</c>, <c>Commandline.cpp</c>, <c>ConptyConnection.cpp</c>): wt splits
/// its arguments into commands at every <c>;</c> not written <c>\;</c> and then turns <c>\;</c> into <c>;</c>; it joins the
/// command's words again with spaces, putting double quotes around a word that holds a space and escaping nothing; it expands
/// <c>%VARIABLES%</c> in that line; docker then splits the line by the C runtime's rules. Every <c>;</c> is therefore written
/// <c>\;</c>, and an argument that one of these steps would change in a way escaping cannot undo is refused (<see cref="Refusal"/>)
/// rather than guessed at. The console window has none of those steps: docker is started directly and gets the list .NET joined,
/// so it refuses only what no command line can hold (<see cref="ConsoleRefusal"/>). Both clear the variables that would point
/// docker at another engine or change its TLS (<see cref="DockerVariables"/>); the command names its engine with <c>-H</c> anyway.
/// </remarks>
public static class ExternalTerminal
{
    /// <summary>Why a command is not started in an external terminal.</summary>
    public const string CannotPassText = "This command cannot be passed to an external terminal unchanged. Copy it instead.";

    /// <summary>Why nothing can be started: the docker CLI is not installed where Windows finds programs.</summary>
    public const string DockerNotFoundText = "The docker command was not found on PATH.";

    /// <summary>The docker CLI's environment variables that would change which engine the command reaches, or how.</summary>
    public static readonly ImmutableArray<string> DockerVariables =
        ["DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH", "DOCKER_TLS"];

    /// <summary>
    /// Null when every argument reaches docker through Windows Terminal unchanged; otherwise <see cref="CannotPassText"/>. Refused:
    /// an empty argument (wt drops it), <c>--</c> (wt's own parser takes it), a double quote (wt does not escape it), <c>%</c> (wt
    /// expands variables), a control character such as a tab or a line break (wt quotes only spaces), half of a surrogate pair,
    /// and an argument with a space that ends in a backslash (the backslash would escape the quote wt closes it with).
    /// </summary>
    public static string? Refusal(IReadOnlyList<string> dockerArguments) =>
        dockerArguments.All(CanPassWindowsTerminal) ? null : CannotPassText;

    /// <summary>
    /// Null when docker started directly gets every argument unchanged; otherwise <see cref="CannotPassText"/>. Refused only: a NUL
    /// (it ends a command line) and half of a surrogate pair (it has no UTF-8 form).
    /// </summary>
    public static string? ConsoleRefusal(IReadOnlyList<string> dockerArguments) =>
        dockerArguments.All(CanPassConsole) ? null : CannotPassText;

    /// <summary>
    /// What to start for <paramref name="dockerArguments"/>, given whether <c>docker.exe</c> and <c>wt.exe</c> were found on
    /// <c>PATH</c>: nothing without docker; with Windows Terminal, its tab when it can carry the command (refused otherwise), then the
    /// console should starting it fail; without it, the console when it can carry the command.
    /// </summary>
    public static ExternalLaunchPlan Plan(string title, ImmutableArray<string> dockerArguments, bool dockerFound, bool windowsTerminalFound)
    {
        if (!dockerFound)
            return new(null, null, DockerNotFoundText);

        var console = ConsoleRefusal(dockerArguments) is null ? OwnConsole(dockerArguments) : null;
        if (!windowsTerminalFound)
            return console is null ? new(null, null, CannotPassText) : new(console, null, null);

        return Refusal(dockerArguments) is { } why
            ? new(null, null, why)
            : new(WindowsTerminal(title, dockerArguments), console, null);
    }

    /// <summary>
    /// <c>wt.exe new-tab --title {title} --suppressApplicationTitle docker {arguments}</c>, every <c>;</c> written <c>\;</c>, without
    /// shell execute. Whether a window or a tab opens follows the user's Windows Terminal settings.
    /// </summary>
    /// <exception cref="ArgumentException">The title is empty, or <see cref="Refusal"/> refuses the arguments.</exception>
    public static ProcessLaunch WindowsTerminal(string title, ImmutableArray<string> dockerArguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        if (Refusal(dockerArguments) is { } why)
            throw new ArgumentException(why, nameof(dockerArguments));
        ImmutableArray<string> arguments = ["new-tab", "--title", title, "--suppressApplicationTitle", "docker", .. dockerArguments];
        return new ProcessLaunch("wt.exe", [.. arguments.Select(EscapeDelimiter)], UseShellExecute: false) { ClearedVariables = DockerVariables };
    }

    /// <summary>
    /// <c>docker.exe</c> with the arguments as they are, started directly (a console program started from a window gets a console
    /// window of its own).
    /// </summary>
    /// <exception cref="ArgumentException"><see cref="ConsoleRefusal"/> refuses the arguments.</exception>
    public static ProcessLaunch OwnConsole(ImmutableArray<string> dockerArguments)
    {
        if (ConsoleRefusal(dockerArguments) is { } why)
            throw new ArgumentException(why, nameof(dockerArguments));
        return new ProcessLaunch("docker.exe", dockerArguments, UseShellExecute: false) { ClearedVariables = DockerVariables };
    }

    /// <summary>
    /// The full path of <paramref name="fileName"/> in the first entry of <paramref name="pathVariable"/> (a <c>PATH</c> value) that
    /// has it, or null. Empty and relative entries are skipped; an entry in double quotes is read without them.
    /// </summary>
    public static string? FindOnPath(string fileName, string? pathVariable, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrEmpty(pathVariable))
            return null;

        foreach (var entry in pathVariable.Split(';'))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
                continue;
            var candidate = Path.Join(directory, fileName);
            if (fileExists(candidate))
                return candidate;
        }

        return null;
    }

    private static string EscapeDelimiter(string argument) => argument.Replace(";", @"\;", StringComparison.Ordinal);

    private static bool CanPassWindowsTerminal(string argument)
    {
        if (argument.Length == 0 || argument == "--")
            return false;
        if (argument.Contains(' ', StringComparison.Ordinal) && argument.EndsWith('\\'))
            return false;

        for (var i = 0; i < argument.Length; i++)
        {
            var c = argument[i];
            if (c is '"' or '%' || char.IsControl(c))
                return false;
            if (char.IsHighSurrogate(c) && i + 1 < argument.Length && char.IsLowSurrogate(argument[i + 1]))
                i++;
            else if (char.IsSurrogate(c))
                return false;
        }

        return true;
    }

    private static bool CanPassConsole(string argument)
    {
        for (var i = 0; i < argument.Length; i++)
        {
            var c = argument[i];
            if (c == '\0')
                return false;
            if (char.IsHighSurrogate(c) && i + 1 < argument.Length && char.IsLowSurrogate(argument[i + 1]))
                i++;
            else if (char.IsSurrogate(c))
                return false;
        }

        return true;
    }
}

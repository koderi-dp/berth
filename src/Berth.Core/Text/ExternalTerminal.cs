using System.Collections.Immutable;

namespace Berth.Text;

/// <summary>A program to start: its file, its arguments one by one (for <c>ProcessStartInfo.ArgumentList</c>), and how.</summary>
public sealed record ProcessLaunch(string FileName, ImmutableArray<string> Arguments, bool UseShellExecute);

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
/// <c>\;</c>, and an argument that one of these steps would change in a way escaping cannot undo is refused
/// (<see cref="Refusal"/>), for the console window too, rather than guessed at. "Copy command" still has every command.
/// </remarks>
public static class ExternalTerminal
{
    /// <summary>Why a command is not started in an external terminal.</summary>
    public const string CannotPassText = "This command cannot be passed to an external terminal unchanged. Copy it instead.";

    /// <summary>
    /// Null when every argument reaches docker unchanged; otherwise <see cref="CannotPassText"/>. Refused: an empty argument (wt drops
    /// it), <c>--</c> (wt's own parser takes it), a double quote (wt does not escape it), <c>%</c> (wt expands variables), a
    /// control character such as a tab or a line break (wt quotes only spaces), half of a surrogate pair, and an argument with a
    /// space that ends in a backslash (the backslash would escape the quote wt closes it with).
    /// </summary>
    public static string? Refusal(IReadOnlyList<string> dockerArguments) =>
        dockerArguments.All(CanPass) ? null : CannotPassText;

    /// <summary>
    /// <c>wt.exe new-tab --title {title} --suppressApplicationTitle docker {arguments}</c>, every <c>;</c> written <c>\;</c>, without
    /// shell execute. Whether a window or a tab opens follows the user's Windows Terminal settings.
    /// </summary>
    /// <exception cref="ArgumentException">The title is empty, or <see cref="Refusal"/> refuses the arguments.</exception>
    public static ProcessLaunch WindowsTerminal(string title, ImmutableArray<string> dockerArguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ThrowIfRefused(dockerArguments);
        ImmutableArray<string> arguments = ["new-tab", "--title", title, "--suppressApplicationTitle", "docker", .. dockerArguments];
        return new ProcessLaunch("wt.exe", [.. arguments.Select(EscapeDelimiter)], UseShellExecute: false);
    }

    /// <summary><c>docker.exe</c> with the arguments as they are, through shell execute, so it gets a console window of its own.</summary>
    /// <exception cref="ArgumentException"><see cref="Refusal"/> refuses the arguments.</exception>
    public static ProcessLaunch OwnConsole(ImmutableArray<string> dockerArguments)
    {
        ThrowIfRefused(dockerArguments);
        return new ProcessLaunch("docker.exe", dockerArguments, UseShellExecute: true);
    }

    private static void ThrowIfRefused(ImmutableArray<string> dockerArguments)
    {
        if (Refusal(dockerArguments) is { } why)
            throw new ArgumentException(why, nameof(dockerArguments));
    }

    private static string EscapeDelimiter(string argument) => argument.Replace(";", @"\;", StringComparison.Ordinal);

    private static bool CanPass(string argument)
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
}

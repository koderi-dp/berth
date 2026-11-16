namespace Berth.Processes;

/// <summary>The WSL distributions <c>wsl.exe -l -q</c> lists, for the connection editor's "Use the WSL default".</summary>
public static class WslDistros
{
    /// <summary>
    /// The names in <paramref name="output"/>, in the order listed (the default first), without blank lines and without Docker
    /// Desktop's own distributions, which run no engine of their own that systemd manages.
    /// </summary>
    public static IReadOnlyList<string> Parse(ReadOnlySpan<byte> output) =>
        [.. ConsoleText.Decode(output)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Trim('\0').Trim())
            .Where(name => name.Length > 0 && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))];
}

namespace Berth.Engine.Models;

/// <summary>
/// What the engine says about one path in a container (the <c>X-Docker-Container-Path-Stat</c> header of the archive calls).
/// <see cref="Mode"/> is Go's <c>os.FileMode</c>: the permission bits, plus a bit for each kind of file that is not a regular one.
/// </summary>
public sealed record PathStat(string Name, long Size, long Mode, DateTimeOffset? Modified, string LinkTarget)
{
    /// <summary>Go's <c>os.ModeDir</c>.</summary>
    public const long DirectoryBit = 1L << 31;

    /// <summary>Go's <c>os.ModeSymlink</c>.</summary>
    public const long SymbolicLinkBit = 1L << 27;

    public bool IsDirectory => (Mode & DirectoryBit) != 0;

    public bool IsLink => (Mode & SymbolicLinkBit) != 0;
}

/// <summary>
/// What a non-TTY exec printed on its standard output, at most the limit it was run with (<see cref="Truncated"/> when it printed
/// more), and its exit code when it ended (none when the output was cut: it was not waited for).
/// </summary>
public sealed record ExecOutput(byte[] Output, bool Truncated, int? ExitCode);

/// <summary>
/// A tar stream of a path in a container, as the engine sends it, and what it says about that path. Read <see cref="Content"/> as it
/// arrives; disposing closes the answer.
/// </summary>
public sealed class ArchiveRead(PathStat? stat, Stream content, IDisposable answer) : IAsyncDisposable
{
    /// <summary>The path the archive holds; null when the engine sent no stat.</summary>
    public PathStat? Stat { get; } = stat;

    public Stream Content { get; } = content;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        answer.Dispose();
    }
}

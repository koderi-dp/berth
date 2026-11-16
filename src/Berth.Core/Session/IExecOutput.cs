namespace Berth.Session;

/// <summary>
/// The terminal's end of an exec stream, attached by the app (<see cref="ContainerDetailSession.AttachExecOutput"/>). The bytes are
/// valid only until the returned task completes. The task completing means the terminal can take more: the pump waits on it, so a
/// terminal that falls behind slows the reads from the engine instead of piling bytes up.
/// </summary>
public interface IExecOutput
{
    /// <param name="generation">The shell the bytes came from (<see cref="ExecState.Generation"/>).</param>
    /// <param name="bytes">The shell's output as read, cut anywhere: never decoded on the way.</param>
    /// <param name="ct">Cancelled when the shell is stopped or the session ends.</param>
    ValueTask WriteAsync(int generation, ReadOnlyMemory<byte> bytes, CancellationToken ct);
}

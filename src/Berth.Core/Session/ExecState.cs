using System.Collections.Immutable;
using Berth.Engine.Models;

namespace Berth.Session;

/// <summary>Where the shell of a container's Exec tab stands.</summary>
public enum ExecStatus { NotStarted, Starting, Running, Ended, Failed }

/// <summary>
/// The shell of a container's Exec tab: its status and the command it was started with, never a byte of what it reads or prints.
/// The command is kept in memory only (for "Starting …" and the hint after a missing shell); it is never logged or saved. Only
/// <see cref="DetailReducer"/> produces new values.
/// </summary>
/// <param name="Generation">Of the latest shell asked for; older results are dropped. 0 before the first.</param>
/// <param name="Command">The command of the latest shell asked for, in memory only; empty before the first.</param>
/// <param name="Size">The terminal's latest size.</param>
/// <param name="ExecId">While Running: the engine's ID of the exec.</param>
/// <param name="ExitCode">When Ended: the shell's exit code, or null when the engine could not say.</param>
/// <param name="HadInput">When Ended: whether anything was typed before it ended.</param>
/// <param name="Error">When Failed: the engine's message, as-is.</param>
public sealed record ExecState(
    ExecStatus Status,
    int Generation,
    ImmutableArray<string> Command,
    TerminalSize Size,
    string? ExecId,
    int? ExitCode,
    bool HadInput,
    string? Error)
{
    private static readonly TerminalSize DefaultSize = new(80, 24);

    /// <summary>No shell asked for yet: generation 0, no command, 80 x 24.</summary>
    public static readonly ExecState None = new(ExecStatus.NotStarted, 0, [], DefaultSize, null, null, false, null);

    /// <summary>The size the start was sent with; a different <see cref="Size"/> once it runs means one resize is owed.</summary>
    public TerminalSize StartedSize { get; init; } = DefaultSize;

    /// <summary>A shell has been asked for and has not ended: Starting or Running.</summary>
    public bool IsActive => Status is ExecStatus.Starting or ExecStatus.Running;
}

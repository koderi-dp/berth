using System.Collections.Immutable;
using Berth.Engine.Models;

namespace Berth.Session;

/// <summary>Something that happened to a detail page: a user request, an engine call's result or a stream message.</summary>
public abstract record DetailEvent;

/// <summary>The page opened: read the container, then start its streams.</summary>
public sealed record DetailOpenRequested : DetailEvent;

/// <summary>Read the container again; its streams restart if it was restarted.</summary>
public sealed record DetailRefreshRequested : DetailEvent;

/// <summary>The user emptied the log view.</summary>
public sealed record LogsClearRequested : DetailEvent;

public sealed record InspectLoaded(int Generation, ContainerInspection Inspection) : DetailEvent;

/// <param name="NotFound">The engine answered 404: the container was removed.</param>
public sealed record InspectFailed(int Generation, string Message, bool NotFound) : DetailEvent;

/// <summary>The log lines collected since the last batch, oldest first.</summary>
public sealed record LogLinesArrived(int Generation, ImmutableArray<LogLine> Lines) : DetailEvent;

/// <summary>The log stream ended: by itself (the container stopped) when <see cref="Error"/> is null, otherwise failed.</summary>
public sealed record LogStreamEnded(int Generation, string? Error) : DetailEvent;

public sealed record StatsSampleArrived(int Generation, StatsSample Sample) : DetailEvent;

/// <summary>The stats stream ended: by itself (the container stopped) when <see cref="Error"/> is null, otherwise failed.</summary>
public sealed record StatsStreamEnded(int Generation, string? Error) : DetailEvent;

/// <summary>The engine connection the page ran on was closed or replaced; the runtime has already stopped its calls.</summary>
public sealed record DetailConnectionLost : DetailEvent;

/// <summary>Open a shell running <see cref="Command"/> (never logged) in the container, sized <see cref="Size"/>.</summary>
public sealed record ExecStartRequested(ImmutableArray<string> Command, TerminalSize Size) : DetailEvent;

/// <summary>End the shell on purpose (the tab was left, or a new one is wanted), or clear how the last one ended.</summary>
public sealed record ExecStopRequested : DetailEvent;

/// <summary>The terminal's size changed.</summary>
public sealed record ExecResizeRequested(TerminalSize Size) : DetailEvent;

/// <summary>The shell of <see cref="Generation"/> was created and its stream is open.</summary>
public sealed record ExecStarted(int Generation, string ExecId) : DetailEvent;

/// <summary>The engine ended the shell's stream: the shell exited, with <see cref="ExitCode"/> when the engine gave one.</summary>
public sealed record ExecEnded(int Generation, int? ExitCode, bool HadInput) : DetailEvent;

/// <summary>Opening the shell or its stream failed; <see cref="Message"/> is the engine's or the connection's, as-is.</summary>
public sealed record ExecFailed(int Generation, string Message) : DetailEvent;

/// <summary>Work <see cref="DetailReducer"/> asks the detail session runtime to carry out.</summary>
public abstract record DetailEffect;

/// <summary>Create and start the shell; answers with ExecStarted, then ExecEnded or ExecFailed, all carrying the same generation.</summary>
public sealed record StartExecEffect(int Generation, ImmutableArray<string> Command, TerminalSize Size) : DetailEffect;

/// <summary>End the current shell on purpose; nothing it still sends is shown.</summary>
public sealed record StopExecEffect : DetailEffect;

/// <summary>
/// End what the shell of <see cref="Generation"/> left in the container (see <see cref="ExecCleanUp"/>): best effort, bounded, silent
/// to the user. Follows the <see cref="StopExecEffect"/> of that shell, or its failure once it ran. With
/// <paramref name="AfterStart"/> (the shell was still starting) it runs once the shell's create and start have returned or been
/// cancelled, and only when the create returned an exec: before that the engine may still start the shell after the clean-up looked.
/// </summary>
public sealed record CleanUpExecEffect(int Generation, bool AfterStart = false) : DetailEffect;

/// <summary>Resize the running shell's terminal.</summary>
public sealed record ResizeExecEffect(string ExecId, TerminalSize Size) : DetailEffect;

/// <summary>Inspect the container; answers with InspectLoaded or InspectFailed carrying the same generation.</summary>
public sealed record InspectContainerEffect(int Generation) : DetailEffect;

/// <summary>
/// Follow the log; answers with LogLinesArrived batches, then LogStreamEnded, all carrying the same generation.
/// <paramref name="Tty"/> is the container's <see cref="ContainerDetails.Tty"/>.
/// </summary>
public sealed record FollowLogsEffect(int Generation, bool Tty, LogRequest Request) : DetailEffect;

/// <summary>Follow the stats; answers with StatsSampleArrived, then StatsStreamEnded, all carrying the same generation.</summary>
public sealed record FollowStatsEffect(int Generation) : DetailEffect;

/// <summary>Stop the log and stats streams started so far; what they still send is ignored.</summary>
public sealed record StopDetailStreamsEffect : DetailEffect;

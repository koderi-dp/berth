using System.Collections.Immutable;
using Berth.Connections;
using Berth.Engine.Models;
using Berth.Text;

namespace Berth.Session;

/// <summary>Something that happened: a user request, an engine call's result, an engine event or a timer.</summary>
public abstract record SessionEvent;

public sealed record ConnectRequested : SessionEvent;

public sealed record ConnectSucceeded(EngineVersion Version, EngineInfo Info) : SessionEvent;

/// <param name="Ssh">Why an SSH connection failed; null for other connections and failures.</param>
public sealed record ConnectFailed(string Message, SshProblem? Ssh = null) : SessionEvent;

/// <param name="Ssh">Why an SSH connection failed; null for other connections and failures.</param>
public sealed record ConnectionLost(string Message, SshProblem? Ssh = null) : SessionEvent;

public sealed record RetryDue : SessionEvent;

public sealed record ContainersLoaded(int Generation, ImmutableArray<ContainerSummary> Containers, EngineInfo Info) : SessionEvent;

/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record LoadFailed(int Generation, string Message, bool EngineUnreachable) : SessionEvent
{
    /// <summary>Why an SSH connection failed under the read; null for other connections and failures.</summary>
    public SshProblem? Ssh { get; init; }
}

public sealed record EngineEventReceived(EngineEventMessage Message) : SessionEvent;

public sealed record ReloadDue : SessionEvent;

/// <summary>The user asked for the list to be read again now (F5).</summary>
public sealed record ReloadRequested : SessionEvent;

/// <param name="Force">Applies to <see cref="ContainerAction.Remove"/>.</param>
public sealed record ActionRequested(ImmutableArray<string> Ids, ContainerAction Action, bool Force = false) : SessionEvent;

public sealed record ActionSucceeded(string Id) : SessionEvent;

/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record ActionFailed(string Id, string Message, bool EngineUnreachable) : SessionEvent;

public sealed record ActionErrorDismissed : SessionEvent;

/// <summary>A screen that shows list stats opened (<paramref name="Wanted"/>) or closed.</summary>
public sealed record StatsWantedChanged(bool Wanted) : SessionEvent;

/// <summary>The latest stats sample of each watched container that sent one since the last batch.</summary>
public sealed record StatsBatchArrived(ImmutableDictionary<string, StatsSample> Latest) : SessionEvent;

/// <summary>The user asked to copy the <c>docker run</c> command of a listed container, written for <paramref name="Shell"/>.</summary>
public sealed record RunCommandRequested(string Id, ShellKind Shell) : SessionEvent;

/// <summary>The container was read for its <c>docker run</c> command.</summary>
public sealed record RunCommandInspected(string Id, ShellKind Shell, ContainerDetails Details) : SessionEvent;

/// <param name="EngineUnreachable">The engine was not reached (<c>EngineException.StatusCode == 0</c>).</param>
public sealed record RunCommandFailed(string Id, string Message, bool EngineUnreachable) : SessionEvent;

/// <summary>Copying to the clipboard failed (another program held it); the error bar says so.</summary>
public sealed record ClipboardCopyFailed : SessionEvent;

/// <summary>"Open in external terminal" could not start a program; the error bar shows <see cref="Message"/> (the start's own words).</summary>
public sealed record ExternalTerminalFailed(string Message) : SessionEvent;

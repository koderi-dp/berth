using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Session;

/// <summary>
/// An engine stand-in for one connection. A test sets the version, container list and inspect answers, makes calls fail, holds
/// calls until it releases them, and pushes messages into the event stream.
/// </summary>
internal sealed class FakeEngineApi : IEngineApi
{
    public static readonly EngineVersion DefaultVersion = new("29.0.0", "1.52", "1.24", "linux", "amd64");
    public static readonly EngineInfo DefaultInfo = new("host", "29.0.0", 8, 16L << 30, 1, 0, 1, 3);

    private readonly ConcurrentDictionary<string, Exception> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ContainerInspection> _inspections = new(StringComparer.Ordinal);
    private readonly Channel<EngineEventMessage> _events = Channel.CreateUnbounded<EngineEventMessage>();
    private readonly TaskCompletionSource _streamStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile IReadOnlyList<ContainerSummary> _containers = [Container("a"), Container("b")];
    private volatile bool _disposed;

    public EngineVersion Version { get; init; } = DefaultVersion;

    public EngineInfo Info { get; init; } = DefaultInfo;

    /// <summary>Held calls and the event stream carry on when the caller cancels, as a slow engine might.</summary>
    public bool IgnoreCancellation { get; init; }

    public IReadOnlyList<ContainerSummary> Containers
    {
        get => _containers;
        set => _containers = value;
    }

    /// <summary>Every call as it was made, for example "Stop a" or "Remove a force=True".</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Held calls that ended because the caller cancelled them.</summary>
    public ConcurrentQueue<string> Cancelled { get; } = new();

    public Task StreamStarted => _streamStarted.Task;

    public bool IsDisposed => _disposed;

    public static ContainerSummary Container(string id) => new(
        id, [$"/{id}"], "nginx:latest", "sha256:1", "nginx", 1_700_000_000, "running", "Up 1 minute", [],
        new Dictionary<string, string>());

    /// <summary>Makes every later <paramref name="call"/> ("Connect", "GetInfo", "List", "Stop", ...) throw.</summary>
    public void Fail(string call, Exception error) => _failures[call] = error;

    /// <summary>Makes every later <paramref name="call"/> wait until the returned gate is released.</summary>
    public Gate Hold(string call) => _gates[call] = new Gate();

    /// <summary>A minimal inspection of <paramref name="id"/> (named after it), running or stopped.</summary>
    public static ContainerInspection Inspection(string id, bool running = true, bool tty = false) => new(
        new ContainerDetails(
            id, id, new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero), "nginx", [],
            new ContainerStateDetails(
                running ? "running" : "exited", running, Paused: false, Restarting: false, ExitCode: 0, Error: "",
                StartedAt: new DateTimeOffset(2026, 11, 15, 8, 0, 1, TimeSpan.Zero),
                FinishedAt: running ? null : new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero),
                Health: null),
            "sha256:1", "nginx:latest", [], ["nginx"], [], "/", "", tty, new Dictionary<string, string>(), [], [], [], "no", "linux"),
        $$"""{"Id":"{{id}}"}""");

    /// <summary>Makes later inspects of <c>inspection.Details.Id</c> answer with <paramref name="inspection"/>.</summary>
    public void SetInspection(ContainerInspection inspection) => _inspections[inspection.Details.Id] = inspection;

    /// <summary>Makes later inspects of <paramref name="id"/> answer with the engine's 404.</summary>
    public void RemoveInspection(string id) => _inspections.TryRemove(id, out _);

    public void Push(EngineEventMessage message) => _events.Writer.TryWrite(message);

    /// <summary>Ends the event stream, normally or with <paramref name="error"/>.</summary>
    public void EndStream(Exception? error = null) => _events.Writer.TryComplete(error);

    public async Task<EngineVersion> ConnectAsync(CancellationToken ct)
    {
        await EnterAsync("Connect", "Connect", ct);
        return Version;
    }

    public async Task<EngineInfo> GetInfoAsync(CancellationToken ct)
    {
        await EnterAsync("GetInfo", "GetInfo", ct);
        return Info;
    }

    public async Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(CancellationToken ct)
    {
        await EnterAsync("List", "List", ct);
        return _containers;
    }

    public Task StartContainerAsync(string id, CancellationToken ct) => EnterAsync("Start", $"Start {id}", ct);

    public Task StopContainerAsync(string id, CancellationToken ct) => EnterAsync("Stop", $"Stop {id}", ct);

    public Task RestartContainerAsync(string id, CancellationToken ct) => EnterAsync("Restart", $"Restart {id}", ct);

    public Task PauseContainerAsync(string id, CancellationToken ct) => EnterAsync("Pause", $"Pause {id}", ct);

    public Task UnpauseContainerAsync(string id, CancellationToken ct) => EnterAsync("Unpause", $"Unpause {id}", ct);

    public Task RemoveContainerAsync(string id, bool force, CancellationToken ct) =>
        EnterAsync("Remove", $"Remove {id} force={force}", ct);

    /// <summary>Answers with the inspection set for <paramref name="id"/>, or the engine's 404 when none is.</summary>
    public async Task<ContainerInspection> InspectContainerAsync(string id, CancellationToken ct)
    {
        await EnterAsync("Inspect", $"Inspect {id}", ct);
        return _inspections.TryGetValue(id, out var inspection)
            ? inspection
            : throw new EngineException(404, $"No such container: {id}");
    }

    public async IAsyncEnumerable<EngineEventMessage> StreamEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Calls.Enqueue("Events");
        _streamStarted.TrySetResult();
        await foreach (var message in _events.Reader.ReadAllAsync(IgnoreCancellation ? CancellationToken.None : ct))
            yield return message;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task EnterAsync(string call, string entry, CancellationToken ct)
    {
        Calls.Enqueue(entry);
        if (_gates.TryGetValue(call, out var gate))
        {
            gate.MarkEntered();
            try
            {
                await gate.Released.WaitAsync(IgnoreCancellation ? CancellationToken.None : ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled.Enqueue(entry);
                throw;
            }
        }

        if (_failures.TryGetValue(call, out var error))
            throw error;
    }

    /// <summary>Holds a call until <see cref="Release"/>; <see cref="Entered"/> completes once the call is waiting.</summary>
    public sealed class Gate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        internal Task Released => _released.Task;

        public void Release() => _released.TrySetResult();

        internal void MarkEntered() => _entered.TrySetResult();
    }
}

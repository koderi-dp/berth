using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Session;

/// <summary>
/// An engine stand-in for one connection. A test sets the version, container list and inspect answers, makes calls fail, holds
/// calls until it releases them, and pushes messages into the event stream, lines into the log stream and samples into the stats
/// stream. Images, volumes, networks, disk use and container create answer from properties and the Set* methods; the pull stream
/// is <see cref="Pulls"/>.
/// </summary>
internal sealed class FakeEngineApi : IEngineApi
{
    public static readonly EngineVersion DefaultVersion = new("29.0.0", "1.52", "1.24", "linux", "amd64");
    public static readonly EngineInfo DefaultInfo = new("host", "29.0.0", 8, 16L << 30, 1, 0, 1, 3);

    private readonly ConcurrentDictionary<string, Exception> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ContainerInspection> _inspections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ImageInspection> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ScriptedStream<PullProgress>> _pullsOf = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ScriptedStream<StatsSample>> _statsOf = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _cancelledWaiters = new(StringComparer.Ordinal);
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

    /// <summary>The log stream: push lines, end it or fail it.</summary>
    public ScriptedStream<LogLine> Logs { get; } = new();

    /// <summary>The stats stream: push samples, end it (the container stopped) or fail it.</summary>
    public ScriptedStream<StatsSample> Stats { get; } = new();

    /// <summary>
    /// The stats stream of container <paramref name="id"/> alone. Once asked for (before the stream opens), stats of that
    /// container read from it instead of <see cref="Stats"/>.
    /// </summary>
    public ScriptedStream<StatsSample> StatsOf(string id) => _statsOf.GetOrAdd(id, _ => new ScriptedStream<StatsSample>());

    /// <summary>Completes once a held call or stream recorded as <paramref name="entry"/> has been cancelled.</summary>
    public Task WhenCancelled(string entry) => CancelledWaiter(entry).Task;

    public bool IsDisposed => _disposed;

    public static ContainerSummary Container(string id) => new(
        id, [$"/{id}"], "nginx:latest", "sha256:1", "nginx", 1_700_000_000, "running", "Up 1 minute", [],
        new Dictionary<string, string>());

    /// <summary>
    /// Makes every later <paramref name="call"/> throw: "Connect", "GetInfo", "List", "Stop", ..., and "ListImages",
    /// "InspectImage", "RemoveImage", "PruneImages", "Pull", "ListVolumes", "CreateVolume", "RemoveVolume", "PruneVolumes",
    /// "ListNetworks", "InspectNetwork", "RemoveNetwork", "PruneNetworks", "DiskUsage", "CreateContainer".
    /// </summary>
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

    /// <summary>Opens after any hold or failure set for "Logs", then yields what the test pushes into <see cref="Logs"/>.</summary>
    public async IAsyncEnumerable<LogLine> StreamLogsAsync(
        string id, bool tty, LogRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var entry = $"Logs {id} tail={request.Tail} follow={request.Follow} tty={tty}";
        await EnterAsync("Logs", entry, ct);
        await foreach (var line in ReadAsync(Logs, entry, ct))
            yield return line;
    }

    /// <summary>Opens after any hold or failure set for "Stats", then yields what the test pushes into <see cref="Stats"/>.</summary>
    public async IAsyncEnumerable<StatsSample> StreamStatsAsync(string id, [EnumeratorCancellation] CancellationToken ct)
    {
        var entry = $"Stats {id}";
        await EnterAsync("Stats", entry, ct);
        var source = _statsOf.TryGetValue(id, out var own) ? own : Stats;
        await foreach (var sample in ReadAsync(source, entry, ct))
            yield return sample;
    }

    /// <summary>What <see cref="ListImagesAsync"/> answers.</summary>
    public IReadOnlyList<ImageSummary> Images { get; set; } = [];

    /// <summary>What <see cref="RemoveImageAsync"/> answers.</summary>
    public IReadOnlyList<ImageDeleteResult> ImageRemoval { get; set; } = [];

    /// <summary>The pull stream: push progress lines, end it, or fail it (an error line or a dropped connection).</summary>
    public ScriptedStream<PullProgress> Pulls { get; } = new();

    /// <summary>
    /// The pull stream of <paramref name="reference"/> alone. Once asked for (before the pull starts), pulls of that reference read
    /// from it instead of <see cref="Pulls"/>, so two pulls at once can be scripted apart.
    /// </summary>
    public ScriptedStream<PullProgress> PullOf(string reference) => _pullsOf.GetOrAdd(reference, _ => new ScriptedStream<PullProgress>());

    public IReadOnlyList<VolumeSummary> Volumes { get; set; } = [];

    public IReadOnlyList<NetworkSummary> Networks { get; set; } = [];

    public DiskUsage DiskUsage { get; set; } = new([], new UsageTotals(0, 0, 0, 0), [], new UsageTotals(0, 0, 0, 0));

    /// <summary>The ID <see cref="CreateContainerAsync"/> answers.</summary>
    public string CreatedId { get; set; } = "created";

    /// <summary>Every container create request, in order.</summary>
    public ConcurrentQueue<ContainerCreateRequest> CreateRequests { get; } = new();

    /// <summary>
    /// Makes later inspects of <paramref name="idOrName"/> answer with <paramref name="details"/> and <paramref name="rawJson"/> (by
    /// default a small object naming the image).
    /// </summary>
    public void SetImage(string idOrName, ImageDetails details, string? rawJson = null) =>
        _images[idOrName] = new ImageInspection(details, rawJson ?? $$"""{"Id":"{{details.Id}}"}""");

    /// <summary>Minimal details of the image <paramref name="id"/> exposing <paramref name="ports"/> ("80/tcp").</summary>
    public static ImageDetails ImageDetails(string id, params string[] ports) => new(
        id, [], [], null, 1000, "amd64", "linux", ports, [], [], [], "/", new Dictionary<string, string>());

    public async Task<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken ct)
    {
        await EnterAsync("ListImages", "ListImages", ct);
        return Images;
    }

    /// <summary>Answers with the inspection set for <paramref name="idOrName"/>, or the engine's 404 when none is.</summary>
    public async Task<ImageInspection> InspectImageAsync(string idOrName, CancellationToken ct)
    {
        await EnterAsync("InspectImage", $"InspectImage {idOrName}", ct);
        return _images.TryGetValue(idOrName, out var inspection)
            ? inspection
            : throw new EngineException(404, $"No such image: {idOrName}");
    }

    public async Task<IReadOnlyList<ImageDeleteResult>> RemoveImageAsync(string idOrName, CancellationToken ct)
    {
        await EnterAsync("RemoveImage", $"RemoveImage {idOrName}", ct);
        return ImageRemoval;
    }

    /// <summary>
    /// Opens after any hold or failure set for "Pull", then yields what the test pushes into <see cref="PullOf"/> that reference, or
    /// else into <see cref="Pulls"/>.
    /// </summary>
    public async IAsyncEnumerable<PullProgress> PullImageAsync(string reference, [EnumeratorCancellation] CancellationToken ct)
    {
        var entry = $"Pull {reference}";
        await EnterAsync("Pull", entry, ct);
        var source = _pullsOf.TryGetValue(reference, out var own) ? own : Pulls;
        await foreach (var line in ReadAsync(source, entry, ct))
            yield return line;
    }

    public async Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct)
    {
        await EnterAsync("ListVolumes", "ListVolumes", ct);
        return Volumes;
    }

    /// <summary>Answers with a local volume of that name.</summary>
    public async Task<VolumeSummary> CreateVolumeAsync(string name, CancellationToken ct)
    {
        await EnterAsync("CreateVolume", $"CreateVolume {name}", ct);
        return new VolumeSummary(name, "local", $"/var/lib/docker/volumes/{name}/_data", null, new Dictionary<string, string>(), "local");
    }

    public Task RemoveVolumeAsync(string name, CancellationToken ct) => EnterAsync("RemoveVolume", $"RemoveVolume {name}", ct);

    public async Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct)
    {
        await EnterAsync("ListNetworks", "ListNetworks", ct);
        return Networks;
    }

    public Task RemoveNetworkAsync(string id, CancellationToken ct) => EnterAsync("RemoveNetwork", $"RemoveNetwork {id}", ct);

    public async Task<DiskUsage> GetDiskUsageAsync(CancellationToken ct)
    {
        await EnterAsync("DiskUsage", "DiskUsage", ct);
        return DiskUsage;
    }

    public async Task<string> CreateContainerAsync(ContainerCreateRequest request, CancellationToken ct)
    {
        await EnterAsync("CreateContainer", $"CreateContainer {request.Image} name={request.Name}", ct);
        CreateRequests.Enqueue(request);
        return CreatedId;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Reads the stream <paramref name="source"/> opens; a cancelled read is recorded in <see cref="Cancelled"/>.</summary>
    private async IAsyncEnumerable<T> ReadAsync<T>(ScriptedStream<T> source, string entry, [EnumeratorCancellation] CancellationToken ct)
    {
        var reader = source.Open();
        var token = IgnoreCancellation ? CancellationToken.None : ct;
        while (true)
        {
            bool more;
            try
            {
                more = await reader.WaitToReadAsync(token);
            }
            catch (OperationCanceledException)
            {
                MarkCancelled(entry);
                throw;
            }

            if (!more)
                yield break;

            while (reader.TryRead(out var item))
                yield return item;
        }
    }

    private void MarkCancelled(string entry)
    {
        Cancelled.Enqueue(entry);
        CancelledWaiter(entry).TrySetResult();
    }

    private TaskCompletionSource CancelledWaiter(string entry) =>
        _cancelledWaiters.GetOrAdd(entry, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

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
                MarkCancelled(entry);
                throw;
            }
        }

        if (_failures.TryGetValue(call, out var error))
            throw error;
    }

    /// <summary>
    /// A stream the test feeds, such as the log stream. Each call that reads it opens a new stream; <see cref="Push"/> and
    /// <see cref="End"/> act on the one opened last, or on the next one when it has ended or none has opened yet (so a test can
    /// script a stream before it opens). Items pushed while a stream is open but no longer read are lost, as they would be.
    /// </summary>
    public sealed class ScriptedStream<T>
    {
        private readonly Lock _lock = new();
        private readonly List<(int Count, TaskCompletionSource Done)> _waiters = [];
        private Channel<T> _current = Channel.CreateUnbounded<T>();
        private bool _taken;
        private bool _ended;
        private int _opened;

        /// <summary>How many times the stream has been opened.</summary>
        public int Opened
        {
            get
            {
                lock (_lock)
                    return _opened;
            }
        }

        /// <summary>Completes once the stream has been opened <paramref name="count"/> times in all.</summary>
        public Task WhenOpened(int count = 1)
        {
            lock (_lock)
            {
                if (_opened >= count)
                    return Task.CompletedTask;

                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, done));
                return done.Task;
            }
        }

        public void Push(T item)
        {
            lock (_lock)
            {
                StartNextIfEnded();
                _current.Writer.TryWrite(item);
            }
        }

        /// <summary>Ends the stream, normally or with <paramref name="error"/>, after the items already pushed.</summary>
        public void End(Exception? error = null)
        {
            lock (_lock)
            {
                StartNextIfEnded();
                _current.Writer.TryComplete(error);
                _ended = true;
            }
        }

        internal ChannelReader<T> Open()
        {
            ChannelReader<T> reader;
            List<TaskCompletionSource> reached;
            lock (_lock)
            {
                if (_taken)
                    StartNext();

                _taken = true;
                _opened++;
                reader = _current.Reader;
                reached = _waiters.Where(w => w.Count <= _opened).Select(w => w.Done).ToList();
                _waiters.RemoveAll(w => w.Count <= _opened);
            }

            reached.ForEach(done => done.TrySetResult());
            return reader;
        }

        private void StartNextIfEnded()
        {
            if (_taken && _ended)
                StartNext();
        }

        private void StartNext()
        {
            _current = Channel.CreateUnbounded<T>();
            _taken = false;
            _ended = false;
        }
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

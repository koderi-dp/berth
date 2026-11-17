using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Berth.Engine.Models;
using Berth.Transport;

namespace Berth.Engine;

/// <summary>
/// The typed client for the Docker Engine API. Plain calls time out after 30 seconds; stop, restart and image and volume removes after
/// 90; streams, a pull and disk usage have no deadline. A timeout or an engine that cannot be reached throws
/// <see cref="EngineException"/> with status 0 (a timeout also with <see cref="EngineException.TimedOut"/>). Disposing the client disposes the handler.
/// </summary>
public sealed partial class EngineClient : IEngineApi
{
    /// <summary>The API from which <c>/system/df</c> takes <c>verbose</c> and then leaves out its older lists.</summary>
    private static readonly ApiVersion DiskUsageVerboseSince = new(1, 52);

    private readonly HttpClient _http;
    private readonly IEngineTransport? _streams;
    private string? _prefix;

    /// <param name="handler">Carries every call but exec start; disposed with the client.</param>
    /// <param name="streams">
    /// Opens the connection of each exec start (see <see cref="StartExecAsync"/>); stays owned by the caller. Without it exec start
    /// throws.
    /// </param>
    public EngineClient(HttpMessageHandler handler, IEngineTransport? streams = null)
    {
        _streams = streams;
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://docker"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>The deadline of a plain call: 30 seconds. Tests shorten it to show which calls have none, without waiting.</summary>
    internal TimeSpan PlainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The deadline of a call that can take long (stop, restart, an image or volume remove): 90 seconds. Tests shorten it.</summary>
    internal TimeSpan LongTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>The API version later calls use; null until <see cref="ConnectAsync"/> succeeds.</summary>
    public ApiVersion? Negotiated { get; private set; }

    public async Task<EngineVersion> ConnectAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "/version", PlainTimeout, ct).ConfigureAwait(false);
        var wire = await ReadAsync(response, EngineJson.Default.WireVersion, ct).ConfigureAwait(false);

        // The engine answered, so these refusals carry its status rather than 0, which means "not reached".
        var status = (int)response.StatusCode;
        var engineApi = wire.ApiVersion ?? "";
        if (!ApiVersion.TryParse(engineApi, out var engineVersion))
        {
            throw new EngineException(status, $"The engine reported an API version Berth cannot read: \"{engineApi}\".");
        }

        if (engineVersion.CompareTo(ApiVersion.Minimum) < 0)
        {
            throw new EngineException(status, $"This engine speaks API {engineVersion}. Berth needs {ApiVersion.Minimum} or newer (Docker 25).");
        }

        var negotiated = engineVersion.CompareTo(ApiVersion.Maximum) > 0 ? ApiVersion.Maximum : engineVersion;
        Negotiated = negotiated;
        _prefix = $"/v{negotiated}";

        return new EngineVersion(wire.Version ?? "", engineApi, wire.MinApiVersion ?? "", wire.Os ?? "", wire.Arch ?? "");
    }

    public async Task<EngineInfo> GetInfoAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, Versioned("/info"), PlainTimeout, ct).ConfigureAwait(false);
        var wire = await ReadAsync(response, EngineJson.Default.WireInfo, ct).ConfigureAwait(false);
        return new EngineInfo(
            wire.Name ?? "",
            wire.ServerVersion ?? "",
            wire.Cpus,
            wire.MemoryBytes,
            wire.ContainersRunning,
            wire.ContainersPaused,
            wire.ContainersStopped,
            wire.Images)
        {
            OperatingSystem = wire.OperatingSystem ?? "",
            KernelVersion = wire.KernelVersion ?? "",
        };
    }

    public async Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, Versioned("/containers/json?all=true"), PlainTimeout, ct).ConfigureAwait(false);
        var wire = await ReadAsync(response, EngineJson.Default.ListWireContainer, ct).ConfigureAwait(false);
        return wire.ConvertAll(ToSummary);
    }

    public Task StartContainerAsync(string id, CancellationToken ct) => ActAsync(HttpMethod.Post, ContainerPath(id, "/start"), PlainTimeout, ct);

    public Task StopContainerAsync(string id, CancellationToken ct) => ActAsync(HttpMethod.Post, ContainerPath(id, "/stop"), LongTimeout, ct);

    public Task RestartContainerAsync(string id, CancellationToken ct) => ActAsync(HttpMethod.Post, ContainerPath(id, "/restart"), LongTimeout, ct);

    public Task PauseContainerAsync(string id, CancellationToken ct) => ActAsync(HttpMethod.Post, ContainerPath(id, "/pause"), PlainTimeout, ct);

    public Task UnpauseContainerAsync(string id, CancellationToken ct) => ActAsync(HttpMethod.Post, ContainerPath(id, "/unpause"), PlainTimeout, ct);

    public Task RemoveContainerAsync(string id, bool force, CancellationToken ct) =>
        ActAsync(HttpMethod.Delete, ContainerPath(id, force ? "?force=true" : ""), PlainTimeout, ct);

    public async Task<ContainerInspection> InspectContainerAsync(string id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, ContainerPath(id, "/json"), PlainTimeout, ct).ConfigureAwait(false);
        var (wire, raw) = await ReadWithRawAsync(response, EngineJson.Default.WireInspect, ct).ConfigureAwait(false);
        return new ContainerInspection(ToDetails(wire), raw);
    }

    /// <summary>
    /// Reads an inspect answer: the record and the engine's whole answer indented (for a raw JSON view). An unreadable or empty answer
    /// throws as <see cref="Unreadable"/> says.
    /// </summary>
    private static async Task<(T Wire, string Raw)> ReadWithRawAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct)
    {
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            var wire = document.Deserialize(type) ?? throw new JsonException("The answer was empty.");
            return (wire, Indented(document));
        }
        catch (JsonException ex)
        {
            throw Unreadable(response, ex);
        }
    }

    /// <summary>
    /// Reads a container's log, stdout and stderr with timestamps, with no deadline. The enumeration ends when the engine closes
    /// the stream; a dropped connection throws <see cref="EngineException"/> with status 0.
    /// </summary>
    public async IAsyncEnumerable<LogLine> StreamLogsAsync(
        string id, bool tty, LogRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var tail = request.Tail < 0 ? "all" : request.Tail.ToString(CultureInfo.InvariantCulture);
        var follow = request.Follow ? "true" : "false";
        var since = request.Since is { } from ? "&since=" + SinceText(from) : "";
        var path = ContainerPath(id, $"/logs?stdout=true&stderr=true&timestamps=true&tail={tail}&follow={follow}{since}");

        using var response = await OpenStreamAsync(HttpMethod.Get, path, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var lines = LogLines.ReadAsync(stream, tty, ct).GetAsyncEnumerator();

        while (await NextAsync(lines, ct).ConfigureAwait(false))
        {
            yield return lines.Current;
        }
    }

    /// <summary>A time as the engine's <c>since</c> takes it: Unix seconds and nanoseconds, "1763370081.123456700".</summary>
    private static string SinceText(DateTimeOffset time)
    {
        var ticks = time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var rest);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds}.{rest * 100:D9}");
    }

    /// <summary>
    /// Reads a container's stats as the engine sends them, one line per sample, with no deadline. A line that is not a sample is
    /// skipped. The enumeration ends at the first line with no read time, which is what the engine keeps sending (on an open
    /// connection) once the container has stopped, or when the engine closes the stream; a dropped connection throws
    /// <see cref="EngineException"/> with status 0.
    /// </summary>
    public async IAsyncEnumerable<StatsSample> StreamStatsAsync(string id, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await OpenStreamAsync(HttpMethod.Get, ContainerPath(id, "/stats?stream=true"), ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var lines = JsonLines.ReadAsync(stream, ct).GetAsyncEnumerator();

        while (await NextAsync(lines, ct).ConfigureAwait(false))
        {
            if (ToStats(lines.Current) is not { } sample)
            {
                continue;
            }

            if (sample.Time == DateTimeOffset.MinValue)
            {
                yield break;
            }

            yield return sample;
        }
    }

    /// <summary>
    /// Reads the engine's events as they happen, with no deadline. A line that is not an event is skipped. The enumeration ends when
    /// the engine closes the stream; a dropped connection throws <see cref="EngineException"/> with status 0.
    /// </summary>
    public async IAsyncEnumerable<EngineEventMessage> StreamEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await OpenStreamAsync(HttpMethod.Get, Versioned("/events"), ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var lines = JsonLines.ReadAsync(stream, ct).GetAsyncEnumerator();

        while (await NextAsync(lines, ct).ConfigureAwait(false))
        {
            if (ToEvent(lines.Current) is { } message)
            {
                yield return message;
            }
        }
    }

    public async Task<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, Versioned("/images/json"), PlainTimeout, ct).ConfigureAwait(false);
        var wire = await ReadAsync(response, EngineJson.Default.ListWireImage, ct).ConfigureAwait(false);
        return wire.ConvertAll(i => new ImageSummary(
            i.Id ?? "", Named(i.RepoTags), Named(i.RepoDigests), i.Created, i.Size, i.Containers is >= 0 ? i.Containers : null));
    }

    public async Task<ImageInspection> InspectImageAsync(string idOrName, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, ImagePath(idOrName, "/json"), PlainTimeout, ct).ConfigureAwait(false);
        var (wire, raw) = await ReadWithRawAsync(response, EngineJson.Default.WireImageInspect, ct).ConfigureAwait(false);
        return new ImageInspection(ToImageDetails(wire), raw);
    }

    private static ImageDetails ToImageDetails(WireImageInspect wire)
    {
        var config = wire.Config;
        return new ImageDetails(
            wire.Id ?? "",
            Named(wire.RepoTags),
            Named(wire.RepoDigests),
            EngineTime.Parse(wire.Created),
            wire.Size,
            wire.Architecture ?? "",
            wire.Os ?? "",
            config?.ExposedPorts?.Keys.ToList() ?? [],
            config?.Env ?? [],
            config?.Cmd ?? [],
            config?.Entrypoint ?? [],
            config?.WorkingDir ?? "",
            config?.Labels ?? new Dictionary<string, string>());
    }

    public async Task<IReadOnlyList<ImageDeleteResult>> RemoveImageAsync(string idOrName, CancellationToken ct)
    {
        var path = ImagePath(idOrName, "");
        using var response = await SendAsync(HttpMethod.Delete, path, LongTimeout, ct).ConfigureAwait(false);
        var wire = await ReadAsync(response, EngineJson.Default.ListWireImageDelete, ct).ConfigureAwait(false);
        return wire.ConvertAll(d => new ImageDeleteResult(d.Untagged, d.Deleted));
    }

    /// <summary>
    /// Pulls an image with no deadline, one item per progress line. A line that is not progress is skipped. An error line throws
    /// <see cref="EngineException"/> with its message and the answer's status (200: the engine reports the failure inside a
    /// successful answer); a dropped connection throws it with status 0.
    /// </summary>
    public async IAsyncEnumerable<PullProgress> PullImageAsync(string reference, [EnumeratorCancellation] CancellationToken ct)
    {
        var image = ImageReference.Parse(reference);
        var path = Versioned($"/images/create?fromImage={Uri.EscapeDataString(image.Name)}&tag={Uri.EscapeDataString(image.Tag)}");

        using var response = await OpenStreamAsync(HttpMethod.Post, path, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var lines = JsonLines.ReadAsync(stream, ct).GetAsyncEnumerator();

        while (await NextAsync(lines, ct).ConfigureAwait(false))
        {
            if (Parse(lines.Current, EngineJson.Default.WirePullLine) is not { } line)
            {
                continue;
            }

            if (line.Error is not null || line.ErrorDetail is not null)
            {
                throw new EngineException((int)response.StatusCode, PullError(line));
            }

            yield return new PullProgress(line.Id ?? "", line.Status ?? "", line.ProgressDetail?.Current, line.ProgressDetail?.Total);
        }
    }

    public async Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, Versioned("/volumes"), PlainTimeout, ct).ConfigureAwait(false);
        var wire = await ReadAsync(response, EngineJson.Default.WireVolumeList, ct).ConfigureAwait(false);
        return wire.Volumes?.ConvertAll(ToVolume) ?? [];
    }

    public async Task<VolumeSummary> CreateVolumeAsync(string name, CancellationToken ct)
    {
        var path = Versioned("/volumes/create");
        var body = JsonBody(new WireVolumeCreate(name), EngineJson.Default.WireVolumeCreate);
        using var response = await SendAsync(HttpMethod.Post, path, PlainTimeout, ct, body).ConfigureAwait(false);
        return ToVolume(await ReadAsync(response, EngineJson.Default.WireVolume, ct).ConfigureAwait(false));
    }

    public Task RemoveVolumeAsync(string name, CancellationToken ct) =>
        ActAsync(HttpMethod.Delete, Versioned($"/volumes/{Uri.EscapeDataString(name)}"), LongTimeout, ct);

    public async Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, Versioned("/networks"), PlainTimeout, ct).ConfigureAwait(false);
        var wire = await ReadAsync(response, EngineJson.Default.ListWireNetwork, ct).ConfigureAwait(false);
        return wire.ConvertAll(ToNetwork);
    }

    public Task RemoveNetworkAsync(string id, CancellationToken ct) => ActAsync(HttpMethod.Delete, NetworkPath(id), PlainTimeout, ct);

    /// <summary>
    /// Reads the disk use of images and volumes with no deadline (only those two kinds are asked for: build cache is the slow one).
    /// From API 1.52 the engine is asked with <c>verbose</c>, so it sends each item once, inside its own totals; an older engine
    /// sends only the lists, and the totals are computed as the docker CLI does.
    /// </summary>
    public async Task<DiskUsage> GetDiskUsageAsync(CancellationToken ct)
    {
        var verbose = Negotiated?.CompareTo(DiskUsageVerboseSince) >= 0 ? "&verbose=true" : "";
        var path = Versioned($"/system/df?type=image&type=volume{verbose}");
        using var response = await OpenStreamAsync(HttpMethod.Get, path, ct).ConfigureAwait(false);
        var wire = await ReadAsync(response, EngineJson.Default.WireDiskUsage, ct).ConfigureAwait(false);
        return DiskUsageMapping.From(wire);
    }

    public async Task<string> CreateContainerAsync(ContainerCreateRequest request, CancellationToken ct)
    {
        var path = Versioned(string.IsNullOrEmpty(request.Name) ? "/containers/create" : $"/containers/create?name={Uri.EscapeDataString(request.Name)}");
        var body = JsonBody(ToWire(request), EngineJson.Default.WireCreateContainer);
        using var response = await SendAsync(HttpMethod.Post, path, PlainTimeout, ct, body).ConfigureAwait(false);
        var wire = await ReadAsync(response, EngineJson.Default.WireCreated, ct).ConfigureAwait(false);
        return wire.Id ?? "";
    }

    /// <summary>
    /// The detach keys every TTY exec is created with. The engine watches a TTY exec's input for a detach sequence and, when the
    /// exec names none, uses Ctrl+P Ctrl+Q (moby <c>daemon/internal/stream/attach.go</c>, <c>defaultEscapeSequence</c>): Ctrl+P
    /// (readline's previous line) was then held back until the next key, and Ctrl+P Ctrl+Q ended the stream with the shell left
    /// running. Detaching cannot be turned off, so the sequence is one nobody types. Only its first key (Ctrl+^, 0x1e) is ever held
    /// back, until the key after it. The syntax is moby/term's <c>ToBytes</c>: <c>ctrl-^</c> is 0x1e, <c>ctrl-@</c> is 0x00.
    /// </summary>
    public const string ExecDetachKeys = "ctrl-^,ctrl-@,ctrl-^,ctrl-@,ctrl-^,ctrl-@";

    public Task<string> CreateExecAsync(
        string containerId, IReadOnlyList<string> command, TerminalSize size, IReadOnlyList<string> environment, CancellationToken ct)
    {
        var consoleSize = ConsoleSize(size);
        var env = environment.Count == 0 ? null : environment;
        return CreateExecAsync(
            containerId, new WireExecCreate(true, true, true, true, command, consoleSize) { Env = env, DetachKeys = ExecDetachKeys }, ct);
    }

    public async Task RunDetachedExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct)
    {
        var execId = await CreateExecAsync(containerId, new WireExecCreate(false, false, false, false, command, null), ct).ConfigureAwait(false);
        var body = JsonBody(new WireExecStart(Detach: true, Tty: false, ConsoleSize: null), EngineJson.Default.WireExecStart);
        using var response = await SendAsync(HttpMethod.Post, ExecPath(execId, "/start"), PlainTimeout, ct, body).ConfigureAwait(false);
    }

    private async Task<string> CreateExecAsync(string containerId, WireExecCreate create, CancellationToken ct)
    {
        var body = JsonBody(create, EngineJson.Default.WireExecCreate);
        using var response = await SendAsync(HttpMethod.Post, ContainerPath(containerId, "/exec"), PlainTimeout, ct, body).ConfigureAwait(false);
        var wire = await ReadAsync(response, EngineJson.Default.WireCreated, ct).ConfigureAwait(false);
        return wire.Id ?? "";
    }

    /// <summary>
    /// Starts an exec on a new connection from the transport given to the constructor: the request is written by hand and the answer
    /// read up to its headers, so the connection can be handed over as an <see cref="ExecStream"/> whose write side can be closed
    /// alone. A client made without that transport throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<ExecStream> StartExecAsync(string execId, TerminalSize size, CancellationToken ct)
    {
        var transport = _streams ?? throw new InvalidOperationException("This client was made without a transport for two-way streams.");
        var path = ExecPath(execId, "/start");
        var body = JsonSerializer.SerializeToUtf8Bytes(new WireExecStart(false, true, ConsoleSize(size)), EngineJson.Default.WireExecStart);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(PlainTimeout);

        Stream? connection = null;
        try
        {
            connection = await transport.OpenAsync(deadline.Token).ConfigureAwait(false);
            var answer = await HttpUpgrade.SendAsync(connection, path, body, deadline.Token).ConfigureAwait(false);
            if (answer.Status == (int)HttpStatusCode.SwitchingProtocols)
            {
                var stream = new UpgradedExecStream(connection, answer.Rest.ToArray());
                connection = null;
                return stream;
            }

            if (answer.Status == (int)HttpStatusCode.OK)
                throw new EngineException(200, "The engine did not switch the connection to a two-way stream.");

            var error = await answer.ReadBodyAsync(deadline.Token).ConfigureAwait(false);
            throw new EngineException(answer.Status, ErrorMessage(answer.Status, answer.Reason, error));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EngineException(0, $"The engine did not answer within {PlainTimeout.TotalSeconds:0} seconds.") { TimedOut = true };
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            throw Unreachable(ex, ct);
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task ResizeExecAsync(string execId, TerminalSize size, CancellationToken ct)
    {
        ConsoleSize(size);
        var rows = size.Rows.ToString(CultureInfo.InvariantCulture);
        var columns = size.Columns.ToString(CultureInfo.InvariantCulture);
        return ActAsync(HttpMethod.Post, ExecPath(execId, $"/resize?h={rows}&w={columns}"), PlainTimeout, ct);
    }

    public async Task<ExecInspection> InspectExecAsync(string execId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, ExecPath(execId, "/json"), PlainTimeout, ct).ConfigureAwait(false);
        var wire = await ReadAsync(response, EngineJson.Default.WireExecInspect, ct).ConfigureAwait(false);
        var running = wire.Running ?? false;
        return new ExecInspection(running, running ? null : wire.ExitCode);
    }

    /// <summary>The engine's <c>ConsoleSize</c>, rows first. A size Berth never sends throws.</summary>
    private static int[] ConsoleSize(TerminalSize size) => size.IsValid
        ? [size.Rows, size.Columns]
        : throw new ArgumentOutOfRangeException(nameof(size), size, "A terminal has 2 to 1000 columns and 1 to 500 rows.");

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private static ContainerSummary ToSummary(WireContainer c) => new(
        c.Id ?? "",
        c.Names ?? [],
        c.Image ?? "",
        c.ImageId ?? "",
        c.Command ?? "",
        c.Created,
        c.State ?? "",
        c.Status ?? "",
        c.Ports?.ConvertAll(p => new PortMapping(p.Ip, p.PrivatePort, p.PublicPort, p.Type ?? "")) ?? [],
        c.Labels ?? new Dictionary<string, string>())
    {
        Mounts = c.Mounts?.ConvertAll(ToMount) ?? [],
        Networks = c.NetworkSettings?.Networks?.Keys.ToList() ?? [],
    };

    private static MountDetails ToMount(WireMount m) =>
        new(m.Type ?? "", m.Source ?? "", m.Destination ?? "", m.Mode ?? "", m.ReadWrite, string.IsNullOrEmpty(m.Name) ? null : m.Name);

    private static ContainerDetails ToDetails(WireInspect c)
    {
        var name = c.Name ?? "";
        var state = c.State;
        var config = c.Config;
        return new ContainerDetails(
            c.Id ?? "",
            name.StartsWith('/') ? name[1..] : name,
            EngineTime.Parse(c.Created) ?? DateTimeOffset.MinValue,
            c.Path ?? "",
            c.Args ?? [],
            new ContainerStateDetails(
                state?.Status ?? "",
                state?.Running ?? false,
                state?.Paused ?? false,
                state?.Restarting ?? false,
                state?.ExitCode ?? 0,
                state?.Error ?? "",
                EngineTime.Parse(state?.StartedAt),
                EngineTime.Parse(state?.FinishedAt),
                state?.Health?.Status),
            c.Image ?? "",
            config?.Image ?? "",
            config?.Env ?? [],
            config?.Cmd ?? [],
            config?.Entrypoint ?? [],
            config?.WorkingDir ?? "",
            config?.User ?? "",
            config?.Tty ?? false,
            config?.Labels ?? new Dictionary<string, string>(),
            c.Mounts?.ConvertAll(ToMount) ?? [],
            ToPorts(c.NetworkSettings?.Ports),
            c.NetworkSettings?.Networks?.Select(n => new NetworkAttachment(
                n.Key, n.Value?.IpAddress ?? "", n.Value?.Gateway ?? "", n.Value?.MacAddress ?? "")).ToList() ?? [],
            c.HostConfig?.RestartPolicy?.Name ?? "",
            c.Platform ?? "")
        {
            CpuLimit = CpuLimit(c.HostConfig),
        };
    }

    /// <summary>The CPUs a container may use: <c>NanoCpus</c> (<c>--cpus</c>), else a positive quota over its period, else none.</summary>
    private static double? CpuLimit(WireHostConfig? host) =>
        host?.NanoCpus is > 0 and var nano ? nano / 1e9
        : host is { CpuQuota: > 0 and var quota, CpuPeriod: > 0 and var period } ? (double)quota / period
        : null;

    /// <summary>
    /// Inspect's port map ("80/tcp" to host bindings) as the list's port mappings: one per binding, or one with no address and no
    /// public port for a port that is exposed but not published. A key that is not "port/protocol" is skipped.
    /// </summary>
    private static List<PortMapping> ToPorts(Dictionary<string, List<WirePortBinding>?>? ports)
    {
        var mappings = new List<PortMapping>();
        foreach (var (key, bindings) in ports ?? [])
        {
            var slash = key.IndexOf('/');
            if (!int.TryParse(slash < 0 ? key : key[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var privatePort))
            {
                continue;
            }

            var type = slash < 0 ? "tcp" : key[(slash + 1)..];
            if (bindings is not { Count: > 0 })
            {
                mappings.Add(new PortMapping(null, privatePort, null, type));
                continue;
            }

            foreach (var binding in bindings)
            {
                int? publicPort = int.TryParse(binding.HostPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : null;
                mappings.Add(new PortMapping(string.IsNullOrEmpty(binding.HostIp) ? null : binding.HostIp, privatePort, publicPort, type));
            }
        }

        return mappings;
    }

    /// <summary>The document laid out two spaces per level with \n line ends, and characters such as &lt; and &amp; left as they are.</summary>
    private static string Indented(JsonDocument document)
    {
        var options = new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            document.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The message of a pull's error line: its details, else its error text, else a fixed text.</summary>
    private static string PullError(WirePullLine line) =>
        !string.IsNullOrWhiteSpace(line.ErrorDetail?.Message) ? line.ErrorDetail.Message
        : !string.IsNullOrWhiteSpace(line.Error) ? line.Error
        : "The engine reported an error while pulling the image.";

    /// <summary>Image tags or digests without the "&lt;none&gt;" placeholders older engines report for an untagged image.</summary>
    private static List<string> Named(List<string>? names) =>
        names?.Where(n => n is not ("<none>:<none>" or "<none>@<none>")).ToList() ?? [];

    private static VolumeSummary ToVolume(WireVolume v) => new(
        v.Name ?? "",
        v.Driver ?? "",
        v.Mountpoint ?? "",
        EngineTime.Parse(v.CreatedAt),
        v.Labels ?? new Dictionary<string, string>(),
        v.Scope ?? "");

    private static NetworkSummary ToNetwork(WireNetwork n) => new(
        n.Id ?? "",
        n.Name ?? "",
        n.Driver ?? "",
        n.Scope ?? "",
        EngineTime.Parse(n.Created),
        n.Internal,
        n.Labels ?? new Dictionary<string, string>(),
        n.Ipam?.Config?.Select(c => c?.Subnet).OfType<string>().Where(s => s.Length > 0).ToList() ?? []);

    /// <summary>The create body: each published port is exposed, and its bindings are grouped under "port/protocol".</summary>
    private static WireCreateContainer ToWire(ContainerCreateRequest request)
    {
        var exposed = new Dictionary<string, WireEmpty>(StringComparer.Ordinal);
        var bindings = new Dictionary<string, List<WirePortBinding>>(StringComparer.Ordinal);
        foreach (var port in request.Ports)
        {
            var key = $"{port.ContainerPort.ToString(CultureInfo.InvariantCulture)}/{(string.IsNullOrEmpty(port.Protocol) ? "tcp" : port.Protocol)}";
            exposed.TryAdd(key, new WireEmpty());
            if (!bindings.TryGetValue(key, out var list))
            {
                bindings[key] = list = [];
            }

            list.Add(new WirePortBinding(port.HostIp ?? "", port.HostPort?.ToString(CultureInfo.InvariantCulture) ?? ""));
        }

        return new WireCreateContainer(request.Image, request.Env, request.Cmd, exposed, new WireCreateHostConfig(request.Binds, bindings));
    }

    private static ByteArrayContent JsonBody<T>(T value, JsonTypeInfo<T> type) => new(JsonSerializer.SerializeToUtf8Bytes(value, type))
    {
        Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
    };

    /// <summary>One line of a stream read as <typeparamref name="T"/>, or null when it is not one.</summary>
    private static T? Parse<T>(ReadOnlyMemory<byte> line, JsonTypeInfo<T> type)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(line.Span, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static StatsSample? ToStats(ReadOnlyMemory<byte> line) =>
        Parse(line, EngineJson.Default.WireStats) is { } wire ? StatsMath.ToSample(wire) : null;

    private static EngineEventMessage? ToEvent(ReadOnlyMemory<byte> line)
    {
        var wire = Parse(line, EngineJson.Default.WireEvent);
        return wire is null
            ? null
            : new EngineEventMessage(
                wire.Type ?? "",
                wire.Action ?? "",
                wire.Actor?.Id ?? "",
                wire.Actor?.Attributes ?? new Dictionary<string, string>(),
                wire.TimeNano);
    }

    /// <summary>Sends a request whose answer is read as it arrives, with no deadline. A 2xx answer is returned; any other status throws.</summary>
    private async Task<HttpResponseMessage> OpenStreamAsync(HttpMethod method, string path, CancellationToken ct, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            throw Unreachable(ex, ct);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await ErrorAsync(response, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Moves to the next item of an open stream; a transport failure mid-read throws <see cref="EngineException"/> with status 0.</summary>
    private static async Task<bool> NextAsync<T>(IAsyncEnumerator<T> items, CancellationToken ct)
    {
        try
        {
            return await items.MoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            throw Unreachable(ex, ct);
        }
    }

    private string ContainerPath(string id, string rest) => Versioned($"/containers/{Uri.EscapeDataString(id)}{rest}");

    private string ImagePath(string idOrName, string rest) => Versioned($"/images/{Uri.EscapeDataString(idOrName)}{rest}");

    private string ExecPath(string id, string rest) => Versioned($"/exec/{Uri.EscapeDataString(id)}{rest}");

    private string NetworkPath(string id) => Versioned($"/networks/{Uri.EscapeDataString(id)}");

    private string Versioned(string path) =>
        (_prefix ?? throw new InvalidOperationException("Connect to the engine before making other calls.")) + path;

    private async Task ActAsync(HttpMethod method, string path, TimeSpan timeout, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request, with <paramref name="content"/> as its body when given (disposed with the request), and buffers the answer.
    /// A 2xx or 304 answer is returned; any other status throws.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, TimeSpan timeout, CancellationToken ct, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EngineException(0, $"The engine did not answer within {timeout.TotalSeconds:0} seconds.") { TimedOut = true };
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            throw Unreachable(ex, ct);
        }

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified)
        {
            return response;
        }

        using (response)
        {
            throw await ErrorAsync(response, ct).ConfigureAwait(false);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, type, ct).ConfigureAwait(false)
                ?? throw new JsonException("The answer was empty.");
        }
        catch (JsonException ex)
        {
            throw Unreadable(response, ex);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            // Only an answer read as it arrives (OpenStreamAsync) can fail here.
            throw Unreachable(ex, ct);
        }
    }

    private static EngineException Unreadable(HttpResponseMessage response, JsonException ex) =>
        new((int)response.StatusCode, $"The engine's answer could not be read: {ex.Message}");

    /// <summary>The engine's own message from an error answer, or the status line when the body carries none.</summary>
    private static async Task<EngineException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new EngineException(status, ErrorMessage(status, response.ReasonPhrase, body));
    }

    /// <summary>The engine's own message in an error answer's body, or the status line when the body carries none.</summary>
    private static string ErrorMessage(int status, string? reason, string body)
    {
        string? message = null;
        try
        {
            message = JsonSerializer.Deserialize(body, EngineJson.Default.WireError)?.Message;
        }
        catch (JsonException)
        {
            // Not JSON: fall back to the status line.
        }

        return string.IsNullOrWhiteSpace(message) ? $"{status} {reason}" : message;
    }

    /// <summary>Whether <paramref name="ex"/> means the engine could not be reached or the connection failed.</summary>
    private static bool IsUnreachable(Exception ex) => ex is HttpRequestException || IsTransportFailure(ex);

    /// <summary>
    /// The <see cref="EngineException"/> with status 0 for a failure that <see cref="IsUnreachable"/> accepts. If the caller has
    /// cancelled, it throws <see cref="OperationCanceledException"/> instead, so cancellation never comes out as an engine failure.
    /// </summary>
    private static EngineException Unreachable(Exception ex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ssh = SshCause(ex);
        return new EngineException(0, ssh?.Message ?? (ex is HttpRequestException http ? TransportMessage(http) : ex.Message)) { Ssh = ssh?.Problem };
    }

    /// <summary>The SSH transport's failure behind <paramref name="ex"/>, or null.</summary>
    private static SshTransportException? SshCause(Exception ex)
    {
        for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
        {
            if (cause is SshTransportException ssh)
                return ssh;
        }

        return null;
    }

    /// <summary>The message of the transport failure behind <paramref name="ex"/>, or of its innermost cause when none is one.</summary>
    private static string TransportMessage(HttpRequestException ex)
    {
        Exception innermost = ex;
        for (var cause = ex.InnerException; cause is not null; cause = cause.InnerException)
        {
            if (IsTransportFailure(cause))
            {
                return cause.Message;
            }

            innermost = cause;
        }

        return innermost.Message;
    }

    private static bool IsTransportFailure(Exception ex) =>
        ex is SocketException or IOException or TimeoutException or AuthenticationException;
}

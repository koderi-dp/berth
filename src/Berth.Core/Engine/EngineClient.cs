using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Berth.Engine.Models;

namespace Berth.Engine;

/// <summary>
/// The typed client for the Docker Engine API. Plain calls time out after 30 seconds, stop and restart after 90; a timeout or an
/// engine that cannot be reached throws <see cref="EngineException"/> with status 0. Disposing the client disposes the handler.
/// </summary>
public sealed class EngineClient : IEngineApi
{
    private static readonly TimeSpan PlainTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(90);

    private readonly HttpClient _http;
    private string? _prefix;

    public EngineClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://docker"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

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
            wire.Images);
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
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        WireInspect wire;
        string raw;
        try
        {
            using var document = JsonDocument.Parse(body);
            wire = document.Deserialize(EngineJson.Default.WireInspect) ?? throw new JsonException("The answer was empty.");
            raw = Indented(document);
        }
        catch (JsonException ex)
        {
            throw Unreadable(response, ex);
        }

        return new ContainerInspection(ToDetails(wire), raw);
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
        var path = ContainerPath(id, $"/logs?stdout=true&stderr=true&timestamps=true&tail={tail}&follow={follow}");

        using var response = await OpenStreamAsync(path, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var lines = LogLines.ReadAsync(stream, tty, ct).GetAsyncEnumerator();

        while (await NextAsync(lines, ct).ConfigureAwait(false))
        {
            yield return lines.Current;
        }
    }

    /// <summary>
    /// Reads a container's stats as the engine sends them, one line per sample, with no deadline. A line that is not a sample is
    /// skipped. The enumeration ends at the first line with no read time, which is what the engine keeps sending (on an open
    /// connection) once the container has stopped, or when the engine closes the stream; a dropped connection throws
    /// <see cref="EngineException"/> with status 0.
    /// </summary>
    public async IAsyncEnumerable<StatsSample> StreamStatsAsync(string id, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await OpenStreamAsync(ContainerPath(id, "/stats?stream=true"), ct).ConfigureAwait(false);
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
        using var response = await OpenStreamAsync(Versioned("/events"), ct).ConfigureAwait(false);
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
        c.Labels ?? new Dictionary<string, string>());

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
            c.Mounts?.ConvertAll(m => new MountDetails(
                m.Type ?? "", m.Source ?? "", m.Destination ?? "", m.Mode ?? "", m.ReadWrite, string.IsNullOrEmpty(m.Name) ? null : m.Name)) ?? [],
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

    private static StatsSample? ToStats(ReadOnlyMemory<byte> line)
    {
        WireStats? wire;
        try
        {
            wire = JsonSerializer.Deserialize(line.Span, EngineJson.Default.WireStats);
        }
        catch (JsonException)
        {
            return null;
        }

        return wire is null ? null : StatsMath.ToSample(wire);
    }

    private static EngineEventMessage? ToEvent(ReadOnlyMemory<byte> line)
    {
        WireEvent? wire;
        try
        {
            wire = JsonSerializer.Deserialize(line.Span, EngineJson.Default.WireEvent);
        }
        catch (JsonException)
        {
            return null;
        }

        return wire is null
            ? null
            : new EngineEventMessage(
                wire.Type ?? "",
                wire.Action ?? "",
                wire.Actor?.Id ?? "",
                wire.Actor?.Attributes ?? new Dictionary<string, string>(),
                wire.TimeNano);
    }

    /// <summary>Sends a GET whose answer is read as it arrives, with no deadline. A 2xx answer is returned; any other status throws.</summary>
    private async Task<HttpResponseMessage> OpenStreamAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

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

    private string Versioned(string path) =>
        (_prefix ?? throw new InvalidOperationException("Connect to the engine before making other calls.")) + path;

    private async Task ActAsync(HttpMethod method, string path, TimeSpan timeout, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Sends a request and buffers the answer. A 2xx or 304 answer is returned; any other status throws.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EngineException(0, $"The engine did not answer within {timeout.TotalSeconds:0} seconds.");
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
    }

    private static EngineException Unreadable(HttpResponseMessage response, JsonException ex) =>
        new((int)response.StatusCode, $"The engine's answer could not be read: {ex.Message}");

    /// <summary>The engine's own message from an error answer, or the status line when the body carries none.</summary>
    private static async Task<EngineException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        string? message = null;
        try
        {
            message = JsonSerializer.Deserialize(body, EngineJson.Default.WireError)?.Message;
        }
        catch (JsonException)
        {
            // Not JSON: fall back to the status line.
        }

        return new EngineException(status, string.IsNullOrWhiteSpace(message) ? $"{status} {response.ReasonPhrase}" : message);
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
        return new EngineException(0, ex is HttpRequestException http ? TransportMessage(http) : ex.Message);
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

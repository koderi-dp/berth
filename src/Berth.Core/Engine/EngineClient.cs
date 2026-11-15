using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
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

    /// <summary>
    /// Reads the engine's events as they happen, with no deadline. A line that is not an event is skipped. The enumeration ends when
    /// the engine closes the stream; a dropped connection throws <see cref="EngineException"/> with status 0.
    /// </summary>
    public async IAsyncEnumerable<EngineEventMessage> StreamEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await OpenStreamAsync(Versioned("/events"), ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var lines = JsonLines.ReadAsync(stream, ct).GetAsyncEnumerator();

        while (await NextLineAsync(lines, ct).ConfigureAwait(false))
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

    /// <summary>Moves to the next line of an open stream; a transport failure mid-read throws <see cref="EngineException"/> with status 0.</summary>
    private static async Task<bool> NextLineAsync(IAsyncEnumerator<ReadOnlyMemory<byte>> lines, CancellationToken ct)
    {
        try
        {
            return await lines.MoveNextAsync().ConfigureAwait(false);
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
            throw new EngineException((int)response.StatusCode, $"The engine's answer could not be read: {ex.Message}");
        }
    }

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

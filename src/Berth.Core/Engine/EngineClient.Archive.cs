using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Berth.Engine.Models;

namespace Berth.Engine;

/// <summary>The archive calls (a path's stat, a tar of it, a tar into a folder) and the non-TTY exec that returns its output.</summary>
public sealed partial class EngineClient
{
    private const string PathStatHeader = "X-Docker-Container-Path-Stat";

    /// <summary><c>HEAD /containers/{id}/archive?path=</c>: the path's stat. 30 s deadline. A missing path throws the engine's 404.</summary>
    public async Task<PathStat> StatPathAsync(string containerId, string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Head, ArchivePath(containerId, path, ""), PlainTimeout, ct).ConfigureAwait(false);
        return ReadStat(response) ?? throw new EngineException((int)response.StatusCode, "The engine did not say what the path is.");
    }

    /// <summary>
    /// <c>GET /containers/{id}/archive?path=</c>: a tar of the path (a folder with everything under it), read as it arrives, with no
    /// deadline. An error answer throws with the engine's message as it is.
    /// </summary>
    public async Task<ArchiveRead> GetArchiveAsync(string containerId, string path, CancellationToken ct)
    {
        var response = await OpenStreamAsync(HttpMethod.Get, ArchivePath(containerId, path, ""), ct).ConfigureAwait(false);
        try
        {
            var stat = ReadStat(response);
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new ArchiveRead(stat, new UnreachableMapping(stream), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <c>PUT /containers/{id}/archive?path=&amp;noOverwriteDirNonDir=true</c>: the tar <paramref name="write"/> writes is unpacked into
    /// the folder <paramref name="path"/>, with no deadline. The files keep the owner the tar names (no <c>copyUIDGID</c>), and a folder
    /// is never replaced by a file or the other way round. <c>Expect: 100-continue</c> lets the engine refuse (no such folder) before
    /// the upload starts, so its message is the one shown. A failure of <paramref name="write"/> itself (a local file that cannot be
    /// read) is thrown as it is, not as an engine failure.
    /// </summary>
    public async Task PutArchiveAsync(string containerId, string path, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        using var content = new PushContent(write);
        using var request = new HttpRequestMessage(HttpMethod.Put, ArchivePath(containerId, path, "&noOverwriteDirNonDir=true")) { Content = content };
        request.Headers.ExpectContinue = true;

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception) when (content.Failure is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
            throw;
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            throw Unreachable(ex, ct);
        }

        using (response)
        {
            if (content.Failure is { } failure)
                ExceptionDispatchInfo.Throw(failure);
            if (!response.IsSuccessStatusCode)
                throw await ErrorAsync(response, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs <paramref name="command"/> in a running container without a terminal, attached to its output only, and returns what it
    /// printed on standard output (standard error is not kept) up to <paramref name="limit"/> bytes, and its exit code. The whole run
    /// has the 30 s deadline. A stopped or paused container throws the engine's 409, a missing program the engine's refusal.
    /// </summary>
    public async Task<ExecOutput> RunExecForOutputAsync(string containerId, IReadOnlyList<string> command, int limit, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(PlainTimeout);
        var token = deadline.Token;
        try
        {
            var execId = await CreateExecAsync(containerId, new WireExecCreate(false, true, true, false, command, null), token).ConfigureAwait(false);
            var body = JsonBody(new WireExecStart(Detach: false, Tty: false, ConsoleSize: null), EngineJson.Default.WireExecStart);
            var output = new MemoryStream();
            var truncated = false;
            using (var response = await OpenStreamAsync(HttpMethod.Post, ExecPath(execId, "/start"), token, body).ConfigureAwait(false))
            {
                var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var frames = LogFrames.ReadAsync(stream, token).GetAsyncEnumerator();
                while (await NextAsync(frames, token).ConfigureAwait(false))
                {
                    var (from, payload) = frames.Current;
                    if (from != LogStream.Stdout)
                        continue;
                    var room = limit - (int)output.Length;
                    if (payload.Length > room)
                    {
                        output.Write(payload.Span[..room]);
                        truncated = true;
                        break;
                    }

                    output.Write(payload.Span);
                }
            }

            var exitCode = truncated ? null : (await InspectExecAsync(execId, token).ConfigureAwait(false)).ExitCode;
            return new ExecOutput(output.ToArray(), truncated, exitCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EngineException(0, $"The engine did not answer within {PlainTimeout.TotalSeconds:0} seconds.") { TimedOut = true };
        }
    }

    private string ArchivePath(string containerId, string path, string rest) =>
        ContainerPath(containerId, $"/archive?path={Uri.EscapeDataString(path)}{rest}");

    /// <summary>The path stat header of an archive answer; null when it is missing or cannot be read.</summary>
    private static PathStat? ReadStat(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(PathStatHeader, out var values) || values.FirstOrDefault() is not { Length: > 0 } header)
            return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(header));
            return JsonSerializer.Deserialize(json, EngineJson.Default.WirePathStat) is { } wire
                ? new PathStat(wire.Name ?? "", wire.Size, wire.Mode, EngineTime.Parse(wire.Mtime), wire.LinkTarget ?? "")
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A request body written by a function as the request is sent (chunked), remembering how that function failed.</summary>
    private sealed class PushContent : HttpContent
    {
        private readonly Func<Stream, CancellationToken, Task> _write;

        public PushContent(Func<Stream, CancellationToken, Task> write)
        {
            _write = write;
            Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");
        }

        /// <summary>What the writing function threw, if it did.</summary>
        public Exception? Failure { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            try
            {
                await _write(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !IsUnreachable(ex))
            {
                Failure = ex;
                throw;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>An archive answer's body whose transport failures read as <see cref="EngineException"/> with status 0, as other streams do.</summary>
    private sealed class UnreachableMapping(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUnreachable(ex))
            {
                throw Unreachable(ex, cancellationToken);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

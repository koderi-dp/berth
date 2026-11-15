using Berth.Transport;

namespace Berth.Engine;

/// <summary>Builds the HTTP handler that carries engine calls over a transport.</summary>
public static class EngineHttp
{
    /// <summary>
    /// A handler whose every new connection is a stream from <paramref name="transport"/>. Requests use the base address
    /// <c>http://docker</c>; the host name is never resolved. The transport stays owned by the caller.
    /// </summary>
    public static HttpMessageHandler CreateHandler(IEngineTransport transport) => new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) => await transport.OpenAsync(ct).ConfigureAwait(false),
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
    };
}

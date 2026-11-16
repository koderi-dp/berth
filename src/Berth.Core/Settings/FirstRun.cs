using System.Net;
using Berth.Connections;

namespace Berth.Settings;

/// <summary>The settings Berth starts with when there are none: one connection, from <c>DOCKER_HOST</c> when it holds an address Berth can use, otherwise the default named pipe.</summary>
public static class FirstRun
{
    private const string LocalName = "Local engine";

    /// <param name="dockerHost">The value of <c>DOCKER_HOST</c>, or null when it is not set.</param>
    public static AppSettings Create(string? dockerHost)
    {
        var connection = FromDockerHost(dockerHost) ?? DefaultPipe();
        return new AppSettings([connection], connection.Id, ContainerListOptions.Default);
    }

    private static ConnectionProfile? FromDockerHost(string? dockerHost)
    {
        if (dockerHost is null || !EngineAddress.TryParse(dockerHost, out var kind, out var host, out var port, out var pipeName, out _))
        {
            return null;
        }

        return new ConnectionProfile(NewId(), NameFor(host), kind, host, port, pipeName, null);
    }

    /// <summary>
    /// "Local engine" for an engine on this machine (localhost, a loopback address, or the pipe host "."), otherwise its host
    /// ("build-host"): the address itself is shown under the name, so the name does not repeat it.
    /// </summary>
    private static string NameFor(string host)
    {
        var local = host is "." or ""
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        return local ? LocalName : host;
    }

    private static ConnectionProfile DefaultPipe() =>
        new(NewId(), LocalName, TransportKind.NamedPipe, ".", 0, EngineAddress.DefaultPipe, null);

    private static string NewId() => Guid.NewGuid().ToString("N");
}

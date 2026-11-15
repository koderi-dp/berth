using Berth.Settings;

namespace Berth.Connections;

/// <summary>The settings Berth starts with when there are none: one connection, from <c>DOCKER_HOST</c> when it holds an address Berth can use, otherwise the default named pipe.</summary>
public static class FirstRun
{
    /// <param name="dockerHost">The value of <c>DOCKER_HOST</c>, or null when it is not set.</param>
    public static AppSettings Create(string? dockerHost)
    {
        var connection = FromDockerHost(dockerHost) ?? DefaultPipe();
        return new AppSettings([connection], connection.Id, new ContainerListOptions(RunningOnly: false));
    }

    private static ConnectionProfile? FromDockerHost(string? dockerHost)
    {
        if (dockerHost is null || !EngineAddress.TryParse(dockerHost, out var kind, out var host, out var port, out var pipeName, out _))
        {
            return null;
        }

        var connection = new ConnectionProfile(NewId(), "", kind, host, port, pipeName, null);
        return connection with { Name = "Docker (" + EngineAddress.Format(connection) + ")" };
    }

    private static ConnectionProfile DefaultPipe() =>
        new(NewId(), "Local engine", TransportKind.NamedPipe, ".", 0, EngineAddress.DefaultPipe, null);

    private static string NewId() => Guid.NewGuid().ToString("N");
}

using Berth.Connections;
using Berth.State;

namespace Berth.Settings;

/// <summary>
/// <c>settings.json</c> as it is read: every field optional, so a file written by an older or newer Berth, or by hand, still
/// reads. <see cref="ToSettings"/> fills in what may be left out and rejects what Berth cannot use. Writing uses
/// <see cref="AppSettings"/> itself, which gives the same JSON.
/// </summary>
internal sealed class SettingsFile
{
    public List<ConnectionEntry?>? Connections { get; set; }

    public string? ActiveConnectionId { get; set; }

    public ContainerListEntry? ContainerList { get; set; }

    public LogsEntry? Logs { get; set; }

    /// <summary>
    /// The settings, or null when they cannot be used: no connection, or a connection without a non-blank id and name, a known
    /// kind, a non-blank host and a port in 1..65535 (TCP) or a non-blank pipe name (named pipe), and a CA file when it has TLS;
    /// or two with the same id.
    /// Left out: <c>tls</c> is none, <c>pipeName</c> is "", <c>port</c> is the kind's default, <c>host</c> of a named pipe is
    /// this machine, <c>containerList</c>, <c>logs</c> and each of their fields is the default (an unknown sort column too),
    /// <c>activeConnectionId</c> is the first connection.
    /// </summary>
    public AppSettings? ToSettings()
    {
        if (Connections is not { Count: > 0 })
            return null;

        var connections = new List<ConnectionProfile>(Connections.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Connections)
        {
            if (entry?.ToProfile() is not { } profile || !ids.Add(profile.Id))
                return null;
            connections.Add(profile);
        }

        var active = connections.Any(c => c.Id == ActiveConnectionId) ? ActiveConnectionId : connections[0].Id;
        return new AppSettings(connections, active, new ContainerListOptions(
            ContainerList?.RunningOnly ?? false,
            ContainerSort.Read(ContainerList?.Sort?.Column, ContainerList?.Sort?.Descending)))
        {
            Logs = new LogViewOptions(
                Logs?.Timestamps ?? LogViewOptions.Default.Timestamps,
                Logs?.Wrap ?? LogViewOptions.Default.Wrap),
            Tray = new TrayOptions(
                Tray?.KeepRunningWhenClosed ?? TrayOptions.Default.KeepRunningWhenClosed,
                Tray?.StillRunningNoticeShown ?? TrayOptions.Default.StillRunningNoticeShown),
        };
    }

    public TrayEntry? Tray { get; set; }

    internal sealed class ConnectionEntry
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public TransportKind? Kind { get; set; }

        public string? Host { get; set; }

        public int? Port { get; set; }

        public string? PipeName { get; set; }

        public TlsEntry? Tls { get; set; }

        public EngineControlEntry? EngineControl { get; set; }

        /// <summary>The profile this entry describes, or null when it is not usable (see <see cref="SettingsFile.ToSettings"/>).</summary>
        public ConnectionProfile? ToProfile() => ToBareProfile() is { } profile ? profile with { EngineControl = EngineControl?.ToControl() } : null;

        private ConnectionProfile? ToBareProfile()
        {
            if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name))
                return null;

            TlsOptions? tls = null;
            if (Tls is not null)
            {
                if (string.IsNullOrWhiteSpace(Tls.CaPath))
                    return null;
                tls = new TlsOptions(Tls.CaPath, Tls.CertPath, Tls.KeyPath);
            }

            switch (Kind)
            {
                case TransportKind.Tcp:
                {
                    var port = Port ?? (tls is null ? EngineAddress.DefaultPort : EngineAddress.DefaultTlsPort);
                    if (string.IsNullOrWhiteSpace(Host) || port is < 1 or > 65535)
                        return null;
                    return new ConnectionProfile(Id, Name, TransportKind.Tcp, Host, port, PipeName ?? "", tls);
                }

                case TransportKind.NamedPipe:
                    if (string.IsNullOrWhiteSpace(PipeName))
                        return null;
                    return new ConnectionProfile(Id, Name, TransportKind.NamedPipe, string.IsNullOrWhiteSpace(Host) ? "." : Host, Port ?? 0, PipeName, tls);

                default:
                    // Missing, or a number that names no kind (the enum converter accepts numbers).
                    return null;
            }
        }
    }

    internal sealed class TlsEntry
    {
        public string? CaPath { get; set; }

        public string? CertPath { get; set; }

        public string? KeyPath { get; set; }
    }

    /// <summary>
    /// Engine control is kept only when both commands are there and each parses (<see cref="CommandLine"/>); otherwise it is dropped and
    /// the connection is kept without it, as a file from before engine control reads.
    /// </summary>
    internal sealed class EngineControlEntry
    {
        public string? StartCommand { get; set; }

        public string? StopCommand { get; set; }

        public EngineControl? ToControl()
        {
            if (StartCommand is not { } start || StopCommand is not { } stop
                || CommandLine.Parse(start).Error is not null || CommandLine.Parse(stop).Error is not null)
                return null;
            return new EngineControl(start, stop);
        }
    }

    internal sealed class TrayEntry
    {
        public bool? KeepRunningWhenClosed { get; set; }

        public bool? StillRunningNoticeShown { get; set; }
    }

    internal sealed class ContainerListEntry
    {
        public bool? RunningOnly { get; set; }

        public SortEntry? Sort { get; set; }
    }

    internal sealed class LogsEntry
    {
        public bool? Timestamps { get; set; }

        public bool? Wrap { get; set; }
    }

    /// <summary>The column is read as text, so a name this Berth does not know falls back to the default instead of failing the file.</summary>
    internal sealed class SortEntry
    {
        public string? Column { get; set; }

        public bool? Descending { get; set; }
    }
}

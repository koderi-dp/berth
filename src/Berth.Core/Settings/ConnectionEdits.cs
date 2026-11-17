using Berth.Connections;

namespace Berth.Settings;

/// <summary>The changes the settings page makes to the saved connections. Each returns new settings; a change that cannot apply returns the same instance.</summary>
public static class ConnectionEdits
{
    /// <summary>Adds <paramref name="profile"/> at the end of the list. The active connection stays.</summary>
    public static AppSettings Add(AppSettings settings, ConnectionProfile profile) =>
        settings with { Connections = [.. settings.Connections, profile] };

    /// <summary>Puts <paramref name="profile"/> in place of the connection with its id. An unknown id changes nothing.</summary>
    public static AppSettings Replace(AppSettings settings, ConnectionProfile profile)
    {
        if (!settings.Connections.Any(connection => connection.Id == profile.Id))
            return settings;

        return settings with { Connections = [.. settings.Connections.Select(connection => connection.Id == profile.Id ? profile : connection)] };
    }

    /// <summary>
    /// Removes the connection <paramref name="id"/>. The last connection is never removed (Berth needs one), and an unknown id changes
    /// nothing. Removing the active connection makes the first remaining one active.
    /// </summary>
    public static AppSettings Remove(AppSettings settings, string id)
    {
        if (settings.Connections.Count <= 1 || !settings.Connections.Any(connection => connection.Id == id))
            return settings;

        ConnectionProfile[] remaining = [.. settings.Connections.Where(connection => connection.Id != id)];
        var active = settings.ActiveConnectionId == id ? remaining[0].Id : settings.ActiveConnectionId;
        return settings with { Connections = remaining, ActiveConnectionId = active };
    }

    /// <summary>
    /// Saves <paramref name="key"/> as accepted on the SSH connection <paramref name="id"/>: only a key for that connection's own host and
    /// port that is not known yet. A changed key is never saved in place of the accepted one (the user removes that first, in the
    /// editor). Anything else changes nothing.
    /// </summary>
    public static AppSettings AcceptHostKey(AppSettings settings, string id, HostKey key)
    {
        if (settings.Connections.FirstOrDefault(connection => connection.Id == id) is not { Ssh: { } ssh } profile || !key.IsFor(profile.Host, profile.Port))
            return settings;

        var accepted = ssh.Accept(key);
        return ReferenceEquals(accepted, ssh) ? settings : Replace(settings, profile with { Ssh = accepted });
    }
}

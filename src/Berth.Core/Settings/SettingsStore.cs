using System.Text.Json;
using Berth.Connections;
using Berth.Storage;

namespace Berth.Settings;

/// <summary>
/// Reads and writes <c>settings.json</c> in one folder. A missing file gives the first-run settings (<see cref="FirstRun"/>). A file that
/// is not valid settings is set aside as <c>settings.bad.json</c> and the first-run settings are used. A file that cannot be read
/// (locked, access denied) is left alone and the first-run settings are used.
/// </summary>
public sealed class SettingsStore
{
    public const string FileName = "settings.json";
    public const string BadFileName = "settings.bad.json";

    private const int ReadAttempts = 5;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(40);

    private readonly string _directory;
    private readonly string _file;
    private readonly string? _dockerHost;

    /// <param name="directory">The folder that holds <c>settings.json</c>.</param>
    /// <param name="dockerHost">The value of <c>DOCKER_HOST</c>, or null: used for the first-run connection.</param>
    public SettingsStore(string directory, string? dockerHost)
    {
        _directory = directory;
        _file = Path.Combine(directory, FileName);
        _dockerHost = dockerHost;
    }

    /// <summary>The saved settings, or the first-run settings when there are none that can be used. Never throws.</summary>
    public AppSettings Load()
    {
        try
        {
            var bytes = ReadFile();
            if (bytes is null)
            {
                return FirstRun.Create(_dockerHost);
            }

            var settings = Parse(bytes);
            if (settings is null)
            {
                SetAsideBadFile();
                return FirstRun.Create(_dockerHost);
            }

            return WithValidActiveConnection(settings);
        }
        catch (Exception)
        {
            return FirstRun.Create(_dockerHost);
        }
    }

    /// <summary>Writes <paramref name="settings"/> atomically: a temp file, flushed to disk, moved over <c>settings.json</c>.</summary>
    public void Save(AppSettings settings) =>
        AtomicFile.Write(_file, stream => JsonSerializer.Serialize(stream, settings, SettingsJson.Default.AppSettings));

    /// <summary>The file's bytes, or null when it is missing or could not be read after the retries (a lock that does not go away).</summary>
    private byte[]? ReadFile()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllBytes(_file);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts)
                {
                    return null;
                }

                Thread.Sleep(ReadRetryDelay);
            }
        }
    }

    /// <summary>The settings in <paramref name="bytes"/>, or null when they are not valid settings.</summary>
    private static AppSettings? Parse(byte[] bytes)
    {
        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize(bytes, SettingsJson.Default.AppSettings);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }

        // The serializer checks the record's own fields; the items of the list are checked here.
        return settings is null || settings.Connections.Any(connection => connection is null) ? null : settings;
    }

    private static AppSettings WithValidActiveConnection(AppSettings settings) =>
        settings.Connections.Any(connection => connection.Id == settings.ActiveConnectionId)
            ? settings
            : settings with { ActiveConnectionId = settings.Connections.FirstOrDefault()?.Id };

    private void SetAsideBadFile()
    {
        try
        {
            File.Move(_file, Path.Combine(_directory, BadFileName), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in place: it is set aside on a later start, or replaced by the next save.
        }
    }
}

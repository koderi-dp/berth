using System.Text.Json;
using Berth.Connections;
using Berth.Storage;

namespace Berth.Settings;

/// <summary>What <see cref="SettingsStore.Load"/> found.</summary>
/// <param name="Settings">The settings to use.</param>
/// <param name="SetAside">A damaged <c>settings.json</c> was moved to <c>settings.bad.json</c> and the first-run settings are used.</param>
/// <param name="SaveError">Why the first-run settings could not be saved, or null. They are used for this run anyway.</param>
public sealed record SettingsLoad(AppSettings Settings, bool SetAside, Exception? SaveError = null);

/// <summary>
/// Reads and writes <c>settings.json</c> in one folder. A missing file gives the first-run settings (<see cref="FirstRun"/>),
/// which are saved at once so their connection id stays the same on the next start. A file that is not usable settings (see
/// <see cref="SettingsFile.ToSettings"/>) is set aside as <c>settings.bad.json</c> and the first-run settings are used and
/// saved in its place. A file that cannot be read (locked, access denied), or a damaged one that cannot be moved, is left
/// alone: the first-run settings are used for this run and not saved over it.
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
    public SettingsLoad Load()
    {
        try
        {
            var (read, bytes) = ReadFile();
            switch (read)
            {
                case ReadOutcome.Missing:
                    return SavedFirstRun(setAside: false);

                case ReadOutcome.Unreadable:
                    return new SettingsLoad(FirstRun.Create(_dockerHost), SetAside: false);
            }

            if (Parse(bytes!) is { } settings)
                return new SettingsLoad(settings, SetAside: false);

            return SetAsideBadFile()
                ? SavedFirstRun(setAside: true)
                : new SettingsLoad(FirstRun.Create(_dockerHost), SetAside: false);
        }
        catch (Exception)
        {
            return new SettingsLoad(FirstRun.Create(_dockerHost), SetAside: false);
        }
    }

    /// <summary>Writes <paramref name="settings"/> atomically: a temp file, flushed to disk, moved over <c>settings.json</c>.</summary>
    public void Save(AppSettings settings) =>
        AtomicFile.Write(_file, stream => JsonSerializer.Serialize(stream, settings, SettingsJson.Default.AppSettings));

    /// <summary>The first-run settings, saved where there is no settings file now; a failed save is reported, not thrown.</summary>
    private SettingsLoad SavedFirstRun(bool setAside)
    {
        var settings = FirstRun.Create(_dockerHost);
        try
        {
            Save(settings);
            return new SettingsLoad(settings, setAside);
        }
        catch (Exception ex)
        {
            return new SettingsLoad(settings, setAside, ex);
        }
    }

    private enum ReadOutcome { Read, Missing, Unreadable }

    /// <summary>The file's bytes; or that it is missing, or could not be read after the retries (a lock that does not go away).</summary>
    private (ReadOutcome Outcome, byte[]? Bytes) ReadFile()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (ReadOutcome.Read, File.ReadAllBytes(_file));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return (ReadOutcome.Missing, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts)
                {
                    return (ReadOutcome.Unreadable, null);
                }

                Thread.Sleep(ReadRetryDelay);
            }
        }
    }

    /// <summary>The settings in <paramref name="bytes"/>, or null when they are not usable settings.</summary>
    private static AppSettings? Parse(byte[] bytes)
    {
        // Many editors (and PowerShell 5's Set-Content -Encoding UTF8) start the file with a UTF-8 byte-order mark; the reader does not skip it.
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith(Utf8Bom))
        {
            json = json[Utf8Bom.Length..];
        }

        try
        {
            return JsonSerializer.Deserialize(json, SettingsJson.Default.SettingsFile)?.ToSettings();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Moves the damaged file to <c>settings.bad.json</c>; false when it could not be moved (it stays where it is).</summary>
    private bool SetAsideBadFile()
    {
        try
        {
            File.Move(_file, Path.Combine(_directory, BadFileName), overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

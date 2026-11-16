using Windows.ApplicationModel;

namespace Berth.Shell;

/// <summary>Where "Start with Windows" stands, as Windows reports the package's startup task.</summary>
internal enum StartWithWindowsState { On, Off, OffInWindows, OffByPolicy, OnByPolicy, Unavailable }

/// <summary>
/// "Start with Windows" through the package's startup task (<c>windows.startupTask</c> in <c>Package.appxmanifest</c>, task
/// <see cref="TaskId"/>). Started that way, Berth starts hidden in the tray (<see cref="SingleInstance"/>). The user can turn the task off
/// in Windows settings, after which only they can turn it on again there; a policy can force it either way.
/// </summary>
internal static class StartWithWindows
{
    public const string TaskId = "BerthStartup";

    /// <summary>The state now; <see cref="StartWithWindowsState.Unavailable"/> when the task cannot be read (no package identity). Never throws.</summary>
    public static async Task<StartWithWindowsState> ReadAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            return Map(task.State);
        }
        catch (Exception ex)
        {
            AppLog.Error("reading the startup task failed", ex);
            return StartWithWindowsState.Unavailable;
        }
    }

    /// <summary>Turns it on (Windows may refuse: then the state says why) or off; returns the state afterwards. Never throws.</summary>
    public static async Task<StartWithWindowsState> SetAsync(bool on)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (on)
                return Map(await task.RequestEnableAsync());
            task.Disable();
            return Map(task.State);
        }
        catch (Exception ex)
        {
            AppLog.Error("changing the startup task failed", ex);
            return StartWithWindowsState.Unavailable;
        }
    }

    /// <summary>What the settings page says under the switch; null when the switch says it all.</summary>
    public static string? Note(StartWithWindowsState state) => state switch
    {
        StartWithWindowsState.On => "Berth starts hidden in the notification area when you sign in.",
        StartWithWindowsState.OffInWindows =>
            "Turned off in Windows settings. To start Berth with Windows, turn it on in Settings > Apps > Startup.",
        StartWithWindowsState.OffByPolicy => "Turned off by a policy on this computer.",
        StartWithWindowsState.OnByPolicy => "Turned on by a policy on this computer.",
        StartWithWindowsState.Unavailable => "Not available: this copy of Berth is not installed as a package.",
        _ => null,
    };

    private static StartWithWindowsState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => StartWithWindowsState.On,
        StartupTaskState.DisabledByUser => StartWithWindowsState.OffInWindows,
        StartupTaskState.DisabledByPolicy => StartWithWindowsState.OffByPolicy,
        StartupTaskState.EnabledByPolicy => StartWithWindowsState.OnByPolicy,
        _ => StartWithWindowsState.Off,
    };
}

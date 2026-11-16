using System.Runtime.InteropServices;
using Microsoft.UI.Reactor;

namespace Berth.Shell;

/// <summary>
/// The one window, as the tray, the single-instance redirection and the close button reach it: show it (restored and in front), hide it
/// to the tray, or quit. Closing it hides it to the tray while the setting is on and the tray icon is there (a hidden window with no
/// icon would leave Berth running with no way back); Quit closes it for good. UI thread only, except <see cref="IsHidden"/>.
/// </summary>
internal static class MainWindow
{
    private static ReactorWindow? _window;
    private static bool _quitting;
    private static volatile bool _hidden;

    /// <summary>Raised on the UI thread when the window is hidden to the tray or shown again.</summary>
    public static event Action? HiddenChanged;

    /// <summary>The window is hidden to the tray (not merely minimized). Safe from any thread.</summary>
    public static bool IsHidden => _hidden;

    /// <summary>Berth was started by Windows at sign-in: the window starts hidden in the tray.</summary>
    public static bool StartHidden { get; set; }

    /// <summary>Takes the window once it exists; hides it at once when Berth was started at sign-in and the tray icon is there.</summary>
    public static void Attach(ReactorWindow window)
    {
        if (_window is not null)
            return;
        _window = window;
        window.Closing += OnClosing;
        if (StartHidden && TrayIcon.IsOpen)
            Hide();
    }

    /// <summary>Shows the window: back from the tray, restored when minimized, activated and in front.</summary>
    public static void Show()
    {
        if (_window is not { } window)
            return;
        window.Show();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window.NativeWindow);
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);
        window.Activate();
        SetForegroundWindow(hwnd);
        SetHidden(false);
    }

    /// <summary>Hides the window to the tray; nothing is closed and the session stays connected.</summary>
    public static void Hide()
    {
        if (_window is not { } window)
            return;
        window.Hide();
        SetHidden(true);
    }

    /// <summary>
    /// Quits for good: the window closes (which ends a shell open in the Exec tab within a second, as on any exit, and removes the tray
    /// icon: <see cref="Program"/>'s window-closed handler).
    /// </summary>
    public static void Quit()
    {
        _quitting = true;
        if (_window is { } window)
            window.Close();
        else
            ReactorApp.Exit();
    }

    private static void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || !AppServices.Current.Settings.Tray.KeepRunningWhenClosed || !TrayIcon.IsOpen)
            return;

        e.Cancel = true;
        Hide();
        TellOnceStillRunning();
    }

    /// <summary>The first time the window is closed to the tray, a notification says Berth is still running there.</summary>
    private static void TellOnceStillRunning()
    {
        var services = AppServices.Current;
        if (services.Settings.Tray.StillRunningNoticeShown)
            return;
        Notifications.Show(
            "Berth is still running",
            "Berth keeps running in the notification area. Right-click its icon for the menu, or choose Quit there to close it.");
        services.UpdateSettings(settings => settings with { Tray = settings.Tray with { StillRunningNoticeShown = true } });
    }

    private static void SetHidden(bool hidden)
    {
        if (_hidden == hidden)
            return;
        _hidden = hidden;
        HiddenChanged?.Invoke();
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}

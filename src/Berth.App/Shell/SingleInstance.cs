using System.Runtime.InteropServices;
using Microsoft.UI.Reactor;
using Microsoft.Windows.AppLifecycle;

namespace Berth.Shell;

/// <summary>
/// One Berth at a time, through the Windows App SDK's app instance redirection: a second launch hands its activation to the first, which
/// shows its window (back from the tray, in front), and exits; so there is never a second window or a second tray icon.
/// </summary>
internal static class SingleInstance
{
    private const string Key = "berth-main";

    /// <summary>
    /// True when this process is the first Berth and goes on; false when another runs, which has been asked to show its window: this
    /// process must exit. Also says whether Windows started Berth at sign-in (<see cref="MainWindow.StartHidden"/>).
    /// </summary>
    public static bool Claim()
    {
        AppInstance main;
        AppActivationArguments activation;
        try
        {
            main = AppInstance.FindOrRegisterForKey(Key);
            activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch (Exception ex)
        {
            // Without the redirection Berth still runs; a second one could start, which is better than none.
            AppLog.Error("claiming the single instance failed", ex);
            return true;
        }

        if (!main.IsCurrent)
        {
            try
            {
                // The first Berth may come to the front: this process was just started by the user and may hand that on.
                AllowSetForegroundWindow((int)main.ProcessId);
                // Not on this thread: the redirection must not wait on a single-threaded apartment.
                Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                AppLog.Error("handing the launch to the running Berth failed", ex);
            }

            return false;
        }

        MainWindow.StartHidden = activation.Kind == ExtendedActivationKind.StartupTask;
        main.Activated += (_, _) => ReactorApp.UIDispatcher?.TryEnqueue(MainWindow.Show);
        return true;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}

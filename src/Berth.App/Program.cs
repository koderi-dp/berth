using Berth;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

#if !DEBUG
// WebView2 reads WEBVIEW2_* variables in any app (WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS can open a remote debugging port, through
// which another process could read the terminal and type into the shell). A Release build drops them before any WebView is made;
// Debug keeps them for the checks.
foreach (var name in Environment.GetEnvironmentVariables().Keys.OfType<string>())
{
    if (name.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase))
        Environment.SetEnvironmentVariable(name, null);
}
#endif

// A second launch shows the first Berth's window and ends here, before it logs or connects anything.
if (!Berth.Shell.SingleInstance.Claim())
    return;

AppLog.Install();
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
        AppLog.Error(e.IsTerminating ? "unhandled exception (terminating)" : "unhandled exception", ex);
};
TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("unobserved task exception", e.Exception);

var services = AppServices.Create(LocalData.Directory, Environment.GetEnvironmentVariable("DOCKER_HOST"));
services.Session.Dispatch(new ConnectRequested());

var window = new WindowSpec
{
    Title = "Berth",
    Width = 1360, // wide enough for the containers table to show every action as a button
    Height = 820,
    Backdrop = BackdropChoice.Of(BackdropKind.Mica),
}.WithPersistence("main");

// An exception in a component's render, an effect, a cleanup or the reconcile is caught by Reactor, never reaches the handlers above,
// and shows as red text in place of the component until a later render succeeds. Logged here; returning null keeps Reactor's own
// panel, so it still shows on screen.
ReactorApp.DefaultRenderErrorHandler = error =>
{
    AppLog.RenderError(error.Source.ToString(), error.ComponentName ?? "?", error.Exception);
    return null;
};

// A shell open in the Exec tab would stay in its container (the engine keeps it): the detail session ends it, given a second at most.
// Berth has one window, so its closing is the exit (closing it to the tray is cancelled before this; Quit closes it for good). The
// tray icon is removed here, so none is left in the notification area.
ReactorApp.WindowClosed += (_, _) =>
{
    services.CloseDetailForExit(TimeSpan.FromSeconds(1));
    Berth.Shell.TrayIcon.Close();
};

ReactorApp.Run<App>(window, _ =>
{
    // Logged; Reactor's own handling of the exception goes on.
    if (Microsoft.UI.Xaml.Application.Current is { } application)
        application.UnhandledException += (_, e) => AppLog.Error("unhandled UI exception", e.Exception);
});

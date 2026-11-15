using Berth;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

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

ReactorApp.Run<App>(window, _ =>
{
    // Logged; Reactor's own handling of the exception goes on.
    if (Microsoft.UI.Xaml.Application.Current is { } application)
        application.UnhandledException += (_, e) => AppLog.Error("unhandled UI exception", e.Exception);
});

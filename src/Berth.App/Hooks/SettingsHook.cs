using Berth.Settings;

namespace Berth.Hooks;

/// <summary>The settings as an external store for <c>UseExternalStore</c>: a subscriber hears of every change made through <see cref="AppServices.UpdateSettings"/>.</summary>
internal static class SettingsHook
{
    public static readonly Func<Action, Action> Subscribe = onChange =>
    {
        var services = AppServices.Current;
        services.SettingsChanged += onChange;
        return () => services.SettingsChanged -= onChange;
    };

    public static readonly Func<AppSettings> Snapshot = () => AppServices.Current.Settings;
}

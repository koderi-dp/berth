using Berth.Shell;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>
/// "Start with Windows": the switch shows the startup task's real state, read when the page is shown, when the window is activated
/// and after each change, and says why
/// when Windows settings or a policy decide it (then the switch is disabled: only Windows settings can change it).
/// </summary>
internal sealed class StartWithWindowsSwitch : Component
{
    public override Element Render()
    {
        // Null until read.
        var (state, setState) = UseState<StartWithWindowsState?>(null);
        var (busy, setBusy) = UseState(false);

        // Read when mounted, and again each time the page is shown or the window activated: the page stays mounted in the navigation
        // cache, and the task can be turned off in Windows settings meanwhile.
        UseEffect(
            () =>
            {
                void Read() => _ = ReadAsync(setState);
                Read();
                StartWithWindows.ReadAgain += Read;
                return () => StartWithWindows.ReadAgain -= Read;
            },
            Array.Empty<object>());

        async Task SetAsync(bool on)
        {
            setBusy(true);
            setState(await StartWithWindows.SetAsync(on));
            setBusy(false);
        }

        var isOn = state is StartWithWindowsState.On or StartWithWindowsState.OnByPolicy;
        var canChange = state is StartWithWindowsState.On or StartWithWindowsState.Off && !busy;
        var note = state is { } known ? StartWithWindows.Note(known) : null;

        return VStack(
            4,
            ToggleSwitch(isOn, on => _ = SetAsync(on), header: "Start with Windows")
                .IsEnabled(canChange)
                .AutomationName("Start with Windows")
                .AutomationId("start-with-windows"),
            note is null
                ? Empty()
                : Caption(note).TextWrapping().Foreground(Theme.SecondaryText).AutomationId("start-with-windows-note").LiveRegion(AutomationLiveSetting.Polite));
    }

    private static async Task ReadAsync(Action<StartWithWindowsState?> setState) => setState(await StartWithWindows.ReadAsync());
}

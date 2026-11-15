using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>Placeholder until the settings page is built.</summary>
internal sealed class SettingsPage : Component
{
    public override Element Render() => Title("Settings").Margin(24, 16, 24, 16);
}

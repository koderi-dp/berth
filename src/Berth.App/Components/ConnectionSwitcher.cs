using Berth.Settings;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>The title bar's list of saved connections; choosing one makes it the active connection.</summary>
internal static class ConnectionSwitcher
{
    public static Element Render(AppSettings settings, Action<string> onSwitch)
    {
        var connections = settings.Connections;
        var names = connections.Select(connection => connection.Name).ToArray();
        var selected = Math.Max(0, IndexOf(settings));

        return ComboBox(names, selected, index =>
            {
                if (index >= 0 && index < connections.Count)
                    onSwitch(connections[index].Id);
            })
            .AutomationName("Connection")
            .Width(240);
    }

    private static int IndexOf(AppSettings settings)
    {
        for (var i = 0; i < settings.Connections.Count; i++)
        {
            if (settings.Connections[i].Id == settings.ActiveConnectionId)
                return i;
        }

        return -1;
    }
}

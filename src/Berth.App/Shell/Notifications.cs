using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Berth.Shell;

/// <summary>
/// Windows notifications under the package's identity. Best effort: a notification that cannot be shown (notifications turned off,
/// no package identity) is logged in fixed words and otherwise ignored; the error bar still has the message.
/// </summary>
internal static class Notifications
{
    /// <summary>Shows <paramref name="title"/> and <paramref name="text"/> (an engine's message as-is). Neither is logged.</summary>
    public static void Show(string title, string text)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">"
                + "<text>" + SecurityElement.Escape(title) + "</text>"
                + "<text>" + SecurityElement.Escape(text) + "</text>"
                + "</binding></visual></toast>");
            ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
        }
        catch (Exception ex)
        {
            AppLog.Error("showing a notification failed", ex);
        }
    }
}

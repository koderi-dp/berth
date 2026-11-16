using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Berth.Shell;

/// <summary>
/// Fits Reactor's tray flyout window to the menu. Reactor makes that window a fixed size in physical pixels, whatever the menu holds
/// and whatever the display scale, so the menu was cut off at 125 % and left empty space below a short one. Each time the menu's size
/// changes, the window takes the menu's size, keeping its lower right corner where Reactor put it (by the pointer, above the taskbar).
/// </summary>
internal static class TrayFlyoutWindow
{
    private const string Title = "ReactorTrayFlyout";

    /// <summary>Follows <paramref name="root"/>, the menu's root element, which must size to its content (aligned left and top).</summary>
    public static void Fit(UIElement root)
    {
        if (root is not FrameworkElement element)
            return;
        element.SizeChanged += (_, e) => Resize(element, e.NewSize);
    }

    private static void Resize(FrameworkElement element, Windows.Foundation.Size size)
    {
        try
        {
            var hwnd = FindFlyout();
            if (hwnd == 0 || element.XamlRoot is not { } root || size.Width <= 0 || size.Height <= 0)
                return;

            var scale = root.RasterizationScale;
            GetWindowRect(hwnd, out var window);
            GetClientRect(hwnd, out var client);
            var extraWidth = window.Right - window.Left - client.Right;
            var extraHeight = window.Bottom - window.Top - client.Bottom;
            var width = (int)Math.Ceiling(size.Width * scale) + extraWidth;
            var height = (int)Math.Ceiling(size.Height * scale) + extraHeight;
            SetWindowPos(hwnd, 0, window.Right - width, window.Bottom - height, width, height, SwpNoZOrder | SwpNoActivate);
        }
        catch (Exception ex)
        {
            AppLog.Error("fitting the tray menu failed", ex);
        }
    }

    private static nint FindFlyout()
    {
        var pid = (uint)Environment.ProcessId;
        nint hwnd = 0;
        while ((hwnd = FindWindowEx(0, hwnd, null, Title)) != 0)
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid)
                return hwnd;
        }

        return 0;
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}

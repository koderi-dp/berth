using Berth.Presentation;
using Microsoft.UI.Reactor;

namespace Berth.Shell;

/// <summary>
/// The tray's icon for each <see cref="TrayIconKind"/>: the app icon's 32 pixel frame with a state dot at its lower right (green running,
/// amber working, red not reachable, grey stopped), cut out of the icon by a transparent ring so it reads at 16 pixels. Made once. When
/// the app icon cannot be read, a plain rounded square stands in for it. The dot colours are the system's status colours: the
/// notification area has no theme tokens.
/// </summary>
internal static class TrayIcons
{
    private const int Size = 32;

    private static readonly Dictionary<TrayIconKind, WindowIcon> Icons = Build();

    public static WindowIcon For(TrayIconKind kind) => Icons[kind];

    private static Dictionary<TrayIconKind, WindowIcon> Build()
    {
        var baseIcon = ReadAppIcon() ?? PlainSquare();
        return new Dictionary<TrayIconKind, WindowIcon>
        {
            [TrayIconKind.Running] = WithDot(baseIcon, 0x6C, 0xCB, 0x5F),
            [TrayIconKind.Working] = WithDot(baseIcon, 0xFC, 0xC4, 0x00),
            [TrayIconKind.Down] = WithDot(baseIcon, 0xE8, 0x40, 0x4A),
            [TrayIconKind.Stopped] = WithDot(baseIcon, 0x9E, 0x9E, 0x9E),
        };
    }

    /// <summary>The 32 by 32 frame of <c>Assets\AppIcon.ico</c> as top-down RGBA, or null when it has none in the plain 32-bit layout.</summary>
    private static byte[]? ReadAppIcon()
    {
        try
        {
            var ico = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
            int count = BitConverter.ToUInt16(ico, 4);
            for (var i = 0; i < count; i++)
            {
                var entry = 6 + 16 * i;
                if (ico[entry] != Size || ico[entry + 1] != Size)
                    continue;
                var offset = (int)BitConverter.ToUInt32(ico, entry + 12);
                var headerSize = BitConverter.ToInt32(ico, offset);
                var bits = BitConverter.ToUInt16(ico, offset + 14);
                if (headerSize != 40 || bits != 32)
                    return null;

                // A DIB: rows bottom-up, pixels BGRA.
                var pixels = new byte[Size * Size * 4];
                var data = offset + headerSize;
                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        var from = data + ((Size - 1 - y) * Size + x) * 4;
                        var to = (y * Size + x) * 4;
                        pixels[to] = ico[from + 2];
                        pixels[to + 1] = ico[from + 1];
                        pixels[to + 2] = ico[from];
                        pixels[to + 3] = ico[from + 3];
                    }
                }

                return pixels;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("reading the app icon for the tray failed", ex);
        }

        return null;
    }

    private static byte[] PlainSquare()
    {
        var pixels = new byte[Size * Size * 4];
        for (var y = 2; y < Size - 2; y++)
        {
            for (var x = 2; x < Size - 2; x++)
            {
                var i = (y * Size + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0xD0;
                pixels[i + 3] = 0xFF;
            }
        }

        return pixels;
    }

    private static WindowIcon WithDot(byte[] baseIcon, byte r, byte g, byte b)
    {
        var pixels = (byte[])baseIcon.Clone();
        const double cx = 24.5, cy = 24.5, dot = 7.0, ring = 9.5;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                var i = (y * Size + x) * 4;
                if (d <= dot)
                {
                    pixels[i] = r;
                    pixels[i + 1] = g;
                    pixels[i + 2] = b;
                    pixels[i + 3] = 0xFF;
                }
                else if (d <= ring)
                {
                    pixels[i + 3] = 0;
                }
            }
        }

        return WindowIcon.FromRgba(pixels, Size, Size);
    }
}

namespace Berth.Files;

/// <summary>
/// How a name from a container becomes a name Windows can hold. Linux allows any character but "/" and NUL; Windows does not. The
/// rules, applied in order: each character below U+0020 and each of <c>&lt; &gt; : " \ | ? *</c> becomes "_" (so no alternate data
/// stream and no drive letter can be named, and a backslash never splits a name into folders); a trailing dot or space becomes "_"
/// (Windows would drop it); a reserved device name (CON, PRN, AUX, NUL, CONIN$, CONOUT$, COM0 to COM9, LPT0 to LPT9, and COM and LPT with ¹ ² ³), alone
/// or before an extension and in any case, gets "_" in front. Anything else is kept as it is.
/// </summary>
public static class WindowsNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>The safe form of <paramref name="name"/> (one segment, not empty) and whether it differs from the name.</summary>
    public static (string Name, bool Changed) Make(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < ' ' || chars[i] is '<' or '>' or ':' or '"' or '\\' or '|' or '?' or '*')
                chars[i] = '_';
        }

        for (var i = chars.Length - 1; i >= 0 && chars[i] is '.' or ' '; i--)
            chars[i] = '_';

        var safe = new string(chars);
        var dot = safe.IndexOf('.');
        var stem = (dot < 0 ? safe : safe[..dot]).TrimEnd(' ');
        if (Reserved.Contains(stem))
            safe = "_" + safe;
        return (safe, !string.Equals(safe, name, StringComparison.Ordinal));
    }
}

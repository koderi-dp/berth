using Berth.Logging;

namespace Berth;

/// <summary>
/// The diagnostics log, <c>berth.log</c> in <see cref="LocalData.Directory"/>. PRIVACY: fixed lifecycle wording and exception type names
/// (<see cref="FileLog.Describe"/>) only. Never a connection address, an error message from the engine or anything about a container.
/// The one exception is <see cref="RenderError"/>: a render error is a programming error in Berth, and its message and stack trace are
/// what is needed to fix it.
/// </summary>
internal static class AppLog
{
    private static readonly FileLog Log = new(LocalData.Directory, "berth.log");

    /// <summary>Writes the start line now and the exit line when the process ends.</summary>
    public static void Install()
    {
        Write("start");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write("exit");
    }

    public static void Write(string line) => Log.Write(line);

    public static void Error(string what, Exception ex) => Log.Write("ERROR " + what + ": " + FileLog.Describe(ex));

    // Render errors already written: Reactor calls its handler again on every render that retries the failing component.
    private static readonly HashSet<string> RenderErrorsWritten = [];

    /// <summary>
    /// Writes an exception Reactor caught in a render, an effect, a cleanup or the reconcile, once per distinct failure: where, the
    /// component, and the exception with its message and stack trace. UI thread only (Reactor calls its handler there).
    /// </summary>
    public static void RenderError(string source, string component, Exception ex)
    {
        var text = ex.ToString();
        if (RenderErrorsWritten.Add(source + "|" + component + "|" + text))
            Log.Write($"ERROR render error ({source}, {component}): {text}");
    }
}

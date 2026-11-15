using Berth.Logging;

namespace Berth;

/// <summary>
/// The diagnostics log, <c>berth.log</c> in <see cref="LocalData.Directory"/>. PRIVACY: fixed lifecycle wording and exception type names
/// (<see cref="FileLog.Describe"/>) only. Never a connection address, an error message from the engine or anything about a container.
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
}

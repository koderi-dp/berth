using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Berth.Session;

/// <summary>
/// Ending a shell the engine keeps. A TTY exec does not end when Berth closes its stream (the shell reads no end of input and the
/// engine does not hang up), so each shell Berth starts carries <c>BERTH_EXEC=&lt;marker&gt;</c> in its environment, which what it
/// starts inherits, and a shell ended on purpose is followed by <see cref="Command"/>: a detached exec in the same container that
/// hangs up every process carrying that exact marker, then kills those still there a second later. The marker is never logged.
/// </summary>
public static class ExecCleanUp
{
    /// <summary>The environment variable that marks a shell Berth started, and everything it started.</summary>
    public const string VariableName = "BERTH_EXEC";

    private const int MarkerLength = 16;

    /// <summary>
    /// The clean-up, for a POSIX <c>sh</c> (busybox, dash, bash). Its first argument is the marker; it is never part of the text.
    /// It checks the marker again (lowercase hex only), finds the processes whose <c>/proc/&lt;pid&gt;/environ</c> holds
    /// <c>BERTH_EXEC=&lt;marker&gt;</c> as a whole entry (never pid 1 or itself; the clean-up's own processes do not carry it), sends
    /// them HUP (an interactive shell ignores TERM), waits a second, and sends KILL to those that are still there. Processes it may
    /// not read are skipped quietly. Exits 0 unless the marker was refused. Line breaks are LF whatever the checkout's are.
    /// </summary>
    public static readonly string Script =
        """
        case "$1" in ""|*[!0-9a-f]*) exit 2;; esac
        m="BERTH_EXEC=$1"
        marked() {
          for d in /proc/[0-9]*; do
            p="${d#/proc/}"
            case "$p" in 1|"$$") continue;; esac
            if { tr '\000' '\n' < "$d/environ"; } 2>/dev/null | grep -qxF -- "$m"; then echo "$p"; fi
          done
        }
        p=$(marked)
        [ -n "$p" ] || exit 0
        kill -HUP $p 2>/dev/null
        sleep 1
        p=$(marked)
        [ -n "$p" ] && kill -KILL $p 2>/dev/null
        exit 0
        """.ReplaceLineEndings("\n");

    /// <summary>A new marker: 16 lowercase hex characters from a cryptographic random source.</summary>
    public static string NewMarker() => RandomNumberGenerator.GetHexString(MarkerLength, lowercase: true);

    /// <summary>The environment entry a shell carries: <c>BERTH_EXEC=&lt;marker&gt;</c>.</summary>
    public static string Variable(string marker) => $"{VariableName}={Checked(marker)}";

    /// <summary>The clean-up's command: <c>sh -c</c> <see cref="Script"/>, with the marker as its one argument.</summary>
    public static ImmutableArray<string> Command(string marker) => ["sh", "-c", Script, "berth-clean-up", Checked(marker)];

    private static string Checked(string marker) =>
        marker.Length == MarkerLength && marker.All(char.IsAsciiHexDigitLower)
            ? marker
            : throw new ArgumentException("A marker is 16 lowercase hex characters.", nameof(marker));
}

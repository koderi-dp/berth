using System.Globalization;
using System.Text;

namespace Berth.Logging;

/// <summary>
/// A small rolling diagnostics log: one file in a folder, at most 1 MB, and one older file (<c>name.1.log</c>) that the next roll
/// replaces. Each line is opened, appended and closed, so another program can read or delete the file at any time. Thread-safe, and
/// it never throws: a line that cannot be written is dropped.
/// </summary>
/// <remarks>
/// PRIVACY: callers pass fixed wording, and exceptions go through <see cref="Describe"/>, which leaves out their messages. Messages can
/// quote addresses, container names or engine output, none of which belongs in the log.
/// </remarks>
public sealed class FileLog
{
    private const long MaxBytes = 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly string _file;
    private readonly string _olderFile;

    public FileLog(string directory, string fileName)
    {
        _directory = directory;
        _file = Path.Combine(directory, fileName);
        _olderFile = Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + ".1" + Path.GetExtension(fileName));
    }

    /// <summary>Appends one line: the time in UTC, then <paramref name="line"/> with any line breaks in it turned into spaces.</summary>
    public void Write(string line)
    {
        try
        {
            var text = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " " + line.ReplaceLineEndings(" ") + Environment.NewLine;
            var bytes = Utf8.GetBytes(text);
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var info = new FileInfo(_file);
                if (info.Exists && info.Length + bytes.Length > MaxBytes)
                    File.Move(_file, _olderFile, overwrite: true);

                using var stream = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
            }
        }
        catch (Exception)
        {
            // Never throws: a log that cannot be written (locked, full disk, no folder) loses the line.
        }
    }

    /// <summary>The type names of an exception and its inner exceptions, outermost first. Never the messages.</summary>
    public static string Describe(Exception ex)
    {
        var text = new StringBuilder();
        Append(text, ex, depth: 0);
        return text.ToString();
    }

    private static void Append(StringBuilder text, Exception ex, int depth)
    {
        text.Append(ex.GetType().FullName);
        if (depth >= 4)
            return;

        IReadOnlyList<Exception> inner = ex is AggregateException aggregate
            ? aggregate.InnerExceptions
            : ex.InnerException is { } single ? [single] : [];
        if (inner.Count == 0)
            return;

        text.Append(" <- (");
        for (var i = 0; i < inner.Count; i++)
        {
            if (i > 0)
                text.Append(", ");
            Append(text, inner[i], depth + 1);
        }

        text.Append(')');
    }
}

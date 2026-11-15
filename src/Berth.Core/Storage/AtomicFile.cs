namespace Berth.Storage;

/// <summary>
/// Writes a file so a crash leaves the old content or the new, never half of either: the content goes to a temp file next to the
/// target, is flushed to disk, and the temp file is moved over the target.
/// </summary>
public static class AtomicFile
{
    /// <summary>Writes through <paramref name="write"/> and replaces <paramref name="path"/>. On failure the temp file is removed, the old file is left as it was, and the exception is rethrown.</summary>
    public static void Write(string path, Action<Stream> write)
    {
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The write already failed; a stray temp file is overwritten by the next write.
        }
    }
}

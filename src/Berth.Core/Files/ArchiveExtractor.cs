using System.Security.Cryptography;

namespace Berth.Files;

/// <summary>
/// Saves a tar stream from a container (one file or one folder with everything under it, as the engine's archive call sends it) into a
/// folder the user chose. The archive is untrusted:
/// <list type="bullet">
/// <item>Every entry must lie under the archive's top entry: an absolute path, a <c>..</c> segment or another top name is skipped
/// (counted as unsafe), and each path is checked once more against the folder after it is put together.</item>
/// <item>Symbolic and hard links are never created and never followed: they are skipped and counted. A file "through" a skipped link
/// lands in an ordinary folder of that name.</item>
/// <item>Devices, FIFOs and other special files are skipped and counted.</item>
/// <item>Names Windows cannot hold are made safe (<see cref="WindowsNames"/>) and counted; two entries that end up with the same name
/// on Windows (which ignores case) keep the first, and the clash is counted.</item>
/// <item>Content is streamed to disk, never held whole. Everything is written under a temporary name beside the target
/// (<c>.berth-…​.partial</c>) and moved to its name at the end; a failure or a cancel removes it, so no partial file is left.</item>
/// <item>Nothing already in the folder is replaced unless <c>replace</c> says so (the user said yes); a folder that is already there
/// then takes the new files and keeps its others, and a file never replaces a folder or the other way round.</item>
/// </list>
/// </summary>
public static class ArchiveExtractor
{
    private const int BufferSize = 81920;

    /// <summary>A download stops when the destination drive has less than this free: 200 MB.</summary>
    public const long FreeSpaceFloor = 200L * 1024 * 1024;

    /// <summary>How much is written between two looks at the free space.</summary>
    private const long FreeSpaceCheckEvery = 1024 * 1024;

    /// <summary>
    /// The free space of the drive that holds <paramref name="folder"/>, read each time it is called. A folder whose free space
    /// cannot be read this way (a network share such as <c>\\server\share</c>, a drive that went away) is taken to have room: the
    /// floor then does not apply, and a full disk shows as the disk's own error.
    /// </summary>
    public static Func<long> FreeSpaceOf(string folder)
    {
        DriveInfo drive;
        try
        {
            drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
        }
        catch (ArgumentException)
        {
            return static () => long.MaxValue;
        }

        return () =>
        {
            try
            {
                return drive.AvailableFreeSpace;
            }
            catch (IOException)
            {
                return long.MaxValue;
            }
            catch (UnauthorizedAccessException)
            {
                return long.MaxValue;
            }
        };
    }

    /// <summary>Why a download stopped for lack of room.</summary>
    public static string FloorText(string folder) =>
        $"Less than 200 MB would be free on {Path.GetPathRoot(Path.GetFullPath(folder))}, so the download stopped. Nothing partial was left.";

    /// <summary>
    /// Saves the archive into <paramref name="destination"/>, reporting each piece of content written to <paramref name="progress"/>.
    /// Throws <see cref="TransferConflictException"/> when the target is there and <paramref name="replace"/> is false,
    /// <see cref="ArchiveFormatException"/> for an archive that cannot be read or whose top entry is not a plain name, and what the
    /// disk throws, and <see cref="DiskSpaceException"/> when the drive has (or comes to have) less than <see cref="FreeSpaceFloor"/>
    /// free (<paramref name="freeSpace"/>, the drive's own by default, read at the start and after each megabyte); in every such case,
    /// and on cancel, nothing of this archive is left under its temporary name. A merge into a folder that is already there moves the
    /// files one by one, so one that fails half way leaves the files moved before it.
    /// </summary>
    public static async Task<TransferSummary> ExtractAsync(
        Stream archive, string destination, bool replace, Action<long> progress, CancellationToken ct, Func<long>? freeSpace = null)
    {
        destination = Path.GetFullPath(destination);
        freeSpace ??= FreeSpaceOf(destination);
        if (freeSpace() < FreeSpaceFloor)
            throw new DiskSpaceException(FloorText(destination));
        progress = Guarded(progress, freeSpace, destination);
        await using var reader = new ArchiveReader(archive);
        var top = await reader.NextAsync(ct).ConfigureAwait(false) ?? throw new ArchiveFormatException("The archive is empty.");
        if (top.Name.StartsWith('/') || Segments(top.Name) is not [var topName] || !IsPlain(topName))
            throw new ArchiveFormatException("The archive does not hold one plain file or folder at its top.");

        var (safeTop, renamed) = WindowsNames.Make(topName);
        var counts = new Counts { Renamed = renamed ? 1 : 0 };
        if (top.Kind is not (ArchiveEntryKind.File or ArchiveEntryKind.Folder))
        {
            counts.Skip(top.Kind);
            return counts.Summary(saved: null);
        }

        var target = Path.Combine(destination, safeTop);
        var staging = Path.Combine(destination, $".berth-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}.partial");
        try
        {
            if (top.Kind == ArchiveEntryKind.File)
            {
                await WriteFileAsync(reader.Data, staging, top, progress, ct).ConfigureAwait(false);
                counts.Files++;
                counts.Bytes += top.Size;
            }
            else
            {
                Directory.CreateDirectory(staging);
                counts.Folders++;
                await ExtractTreeAsync(reader, topName, staging, counts, progress, ct).ConfigureAwait(false);
            }

            Commit(staging, target, top.Kind == ArchiveEntryKind.Folder, replace, safeTop, counts);
            return counts.Summary(target);
        }
        catch
        {
            Remove(staging);
            throw;
        }
    }

    /// <summary><paramref name="progress"/>, with a look at the free space after each megabyte written; under the floor it throws.</summary>
    private static Action<long> Guarded(Action<long> progress, Func<long> freeSpace, string destination)
    {
        long sinceCheck = 0;
        return written =>
        {
            progress(written);
            sinceCheck += written;
            if (sinceCheck < FreeSpaceCheckEvery)
                return;
            sinceCheck = 0;
            if (freeSpace() < FreeSpaceFloor)
                throw new DiskSpaceException(FloorText(destination));
        };
    }

    private static async Task ExtractTreeAsync(
        ArchiveReader reader, string topName, string staging, Counts counts, Action<long> progress, CancellationToken ct)
    {
        // Names already saved in this archive, as Windows compares them.
        var saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = staging + Path.DirectorySeparatorChar;
        while (await reader.NextAsync(ct).ConfigureAwait(false) is { } entry)
        {
            if (entry.Name.StartsWith('/') || Segments(entry.Name) is not [var first, .. var rest] || first != topName || rest.Any(s => s == ".."))
            {
                counts.Unsafe++;
                continue;
            }

            if (rest.Length == 0)
                continue;

            var safe = new string[rest.Length];
            var renamed = false;
            for (var i = 0; i < rest.Length; i++)
            {
                (safe[i], var changed) = WindowsNames.Make(rest[i]);
                renamed |= changed;
            }

            var path = Path.GetFullPath(Path.Combine([staging, .. safe]));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                counts.Unsafe++;
                continue;
            }

            if (entry.Kind is not (ArchiveEntryKind.File or ArchiveEntryKind.Folder))
            {
                counts.Skip(entry.Kind);
                continue;
            }

            var relative = string.Join('/', safe);
            if (entry.Kind == ArchiveEntryKind.Folder)
            {
                if (File.Exists(path) || !TryCreateFolder(path))
                {
                    counts.Clashes++;
                    continue;
                }

                if (saved.Add(relative))
                    counts.Folders++;
                counts.Renamed += renamed ? 1 : 0;
                continue;
            }

            if (saved.Contains(relative) || File.Exists(path) || Directory.Exists(path) || !TryCreateFolder(Path.GetDirectoryName(path)!))
            {
                counts.Clashes++;
                continue;
            }

            await WriteFileAsync(reader.Data, path, entry, progress, ct).ConfigureAwait(false);
            saved.Add(relative);
            counts.Files++;
            counts.Bytes += entry.Size;
            counts.Renamed += renamed ? 1 : 0;
        }
    }

    /// <summary>Creates a folder and those above it; false when a file is in the way.</summary>
    private static bool TryCreateFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (IOException) when (!Directory.Exists(path))
        {
            return false;
        }
    }

    private static async Task WriteFileAsync(Stream? data, string path, ArchiveEntry entry, Action<long> progress, CancellationToken ct)
    {
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            if (data is not null)
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await data.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress(read);
                }
            }
        }

        if (entry.Modified.Year > 1601)
            File.SetLastWriteTimeUtc(path, entry.Modified.UtcDateTime);
    }

    /// <summary>Moves what was saved under its temporary name to its own name, as <paramref name="replace"/> allows.</summary>
    private static void Commit(string staging, string target, bool folder, bool replace, string name, Counts counts)
    {
        var fileThere = File.Exists(target);
        var folderThere = Directory.Exists(target);
        if (!fileThere && !folderThere)
        {
            if (folder)
                Directory.Move(staging, target);
            else
                File.Move(staging, target);
            return;
        }

        if (folderThere && new DirectoryInfo(target).LinkTarget is not null)
            throw new TransferConflictException($"\"{name}\" is already in the folder as a link or junction, so nothing was put into it.");
        if (folder ? fileThere : folderThere)
            throw new TransferConflictException($"\"{name}\" is already in the folder as a {(fileThere ? "file" : "folder")}, so it was not replaced.");
        if (!replace)
            throw new TransferConflictException($"\"{name}\" is already in the folder.");

        if (!folder)
        {
            File.Move(staging, target, overwrite: true);
            return;
        }

        Merge(staging, target, counts);
        Remove(staging);
    }

    /// <summary>
    /// Moves each saved file into the folder that is already there, replacing a file of the same name. A folder of the same name as a
    /// new file, a file in the way of a new folder, and a link or junction on the way (it could lead elsewhere) are left alone and
    /// counted.
    /// </summary>
    private static void Merge(string from, string to, Counts counts)
    {
        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            var there = Path.Combine(to, Path.GetFileName(folder));
            if (File.Exists(there))
            {
                counts.Clashes++;
                continue;
            }

            if (Directory.Exists(there) && LocalFiles.IsLink(new DirectoryInfo(there)))
            {
                counts.Unsafe++;
                continue;
            }

            Directory.CreateDirectory(there);
            Merge(folder, there, counts);
        }

        foreach (var file in Directory.EnumerateFiles(from))
        {
            var there = Path.Combine(to, Path.GetFileName(file));
            if (Directory.Exists(there))
            {
                counts.Clashes++;
                continue;
            }

            File.Move(file, there, overwrite: true);
        }
    }

    /// <summary>Removes what was saved under the temporary name. It holds no links, so removing it never reaches outside it.</summary>
    private static void Remove(string staging)
    {
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            else
                File.Delete(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind under its temporary name; nothing more can be done.
        }
    }

    /// <summary>The segments of a tar name, without empty and "." ones; ".." is kept, for the caller to refuse.</summary>
    private static string[] Segments(string name) => [.. name.Split('/').Where(s => s is not ("" or "."))];

    private static bool IsPlain(string segment) => segment != "..";

    private sealed class Counts
    {
        public int Files;
        public int Folders;
        public long Bytes;
        public int Links;
        public int Special;
        public int Unsafe;
        public int Renamed;
        public int Clashes;

        public void Skip(ArchiveEntryKind kind)
        {
            if (kind is ArchiveEntryKind.SymbolicLink or ArchiveEntryKind.HardLink)
                Links++;
            else
                Special++;
        }

        public TransferSummary Summary(string? saved) => new(Files, Folders, Bytes, Links, Special, Unsafe, Renamed, Clashes, saved);
    }
}

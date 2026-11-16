using System.Collections.Immutable;

namespace Berth.Files;

/// <summary>What an entry of a folder is, as the Files tab shows it.</summary>
public enum FileKind { File, Folder, Link, Other }

/// <summary>How a folder was listed: by a command run in the running container, or from the headers of the folder's archive.</summary>
public enum ListingWay { Exec, Archive }

/// <summary>
/// One entry of a folder: its name (any character but "/" and NUL), what it is, a file's size (none for a folder, a link or a special
/// file), when it was modified, its mode as <c>ls -l</c> writes it ("drwxr-xr-x"), and a link's target when it was read.
/// </summary>
public sealed record FileEntry(string Name, FileKind Kind, long? Size, DateTimeOffset? Modified, string Mode, string? LinkTarget);

/// <summary>
/// A folder's entries in the order they were read. <see cref="Incomplete"/>: some were not read (the archive listing ran out of its
/// budget, or the exec listing could not read them all); <see cref="Capped"/>: there were more than <see cref="EntryLimit"/>, and only
/// the first are here.
/// </summary>
public sealed record FileListing(ContainerPath Path, ImmutableArray<FileEntry> Entries, ListingWay Way, bool Incomplete, bool Capped)
{
    /// <summary>The most entries a listing keeps.</summary>
    public const int EntryLimit = 10_000;
}

/// <summary>The path of a listing is a file, not a folder.</summary>
public sealed class NotAFolderException(ContainerPath path) : Exception($"{path.Value} is not a folder.")
{
    public ContainerPath Path { get; } = path;
}

/// <summary>A mode as <c>ls -l</c> writes it: the kind's letter, then read, write and run for owner, group and others.</summary>
public static class ModeText
{
    /// <summary>From a <c>st_mode</c> (the kind in its top bits, as <c>stat -c %f</c> prints it in hex).</summary>
    public static string FromStatMode(int mode)
    {
        var letter = (mode & 0xF000) switch
        {
            0x4000 => 'd',
            0xA000 => 'l',
            0x2000 => 'c',
            0x6000 => 'b',
            0x1000 => 'p',
            0xC000 => 's',
            _ => '-',
        };
        return Format(letter, mode & 0xFFF);
    }

    /// <summary>From a kind's letter and the twelve permission bits (set-user-ID, set-group-ID, sticky, then rwx three times).</summary>
    public static string Format(char letter, int permissions)
    {
        Span<char> text = stackalloc char[10];
        text[0] = letter;
        for (var i = 0; i < 3; i++)
        {
            var bits = (permissions >> (6 - (3 * i))) & 7;
            text[1 + (3 * i)] = (bits & 4) != 0 ? 'r' : '-';
            text[2 + (3 * i)] = (bits & 2) != 0 ? 'w' : '-';
            var special = (permissions & (0x800 >> i)) != 0;
            var run = (bits & 1) != 0;
            text[3 + (3 * i)] = special
                ? (i == 2 ? (run ? 't' : 'T') : (run ? 's' : 'S'))
                : (run ? 'x' : '-');
        }

        return new string(text);
    }
}

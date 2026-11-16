using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Berth.Engine.Models;

namespace Berth.Files;

/// <summary>
/// Lists a folder of a running container with one non-TTY exec of <c>sh</c>, read without guessing although a name may hold spaces,
/// newlines and any byte but "/" and NUL. The command is the same on busybox (alpine, busybox) and GNU (debian, ubuntu): it was checked
/// against alpine, ubuntu 24.04 and busybox 1.36, which print the same bytes.
/// <code>
/// command -v find &amp;&amp; command -v stat &amp;&amp; command -v readlink || exit 127
/// cd -- "$1" || exit 3
/// find . -mindepth 1 -maxdepth 1 -exec stat -c '%f/%s/%Y/%n/' {} + || exit 4
/// printf '\0'
/// find . -mindepth 1 -maxdepth 1 -type l -exec sh -c 'for l; do printf "%s\0" "$l" "$(readlink "$l")"; done' sh {} + || exit 5
/// </code>
/// The folder is <c>$1</c>, an argument: never part of the script. First part: one record per entry, <c>mode-hex/size/mtime/./name/</c>
/// and a newline: since no name holds "/", the name is everything between "./" and the next "/", whatever it holds, and the newline
/// after that slash ends the record. Then a NUL, which the first part cannot hold. Second part: each link's "./name" and its target,
/// each ended by a NUL, which neither can hold.
/// <para>Limits: a link target that ends in newlines loses them (the shell's command substitution drops them); bytes that are not
/// UTF-8 read as U+FFFD, so such a name is shown but cannot be opened or downloaded by name; the exec runs as the container's user, so
/// a folder that user cannot enter falls back to the archive listing. <c>find</c> and <c>stat</c> run in the container's own
/// <c>sh</c>; an image without them (distroless, scratch) uses the archive listing.</para>
/// </summary>
public static class ExecListing
{
    /// <summary>The most output read: about 25,000 entries with long names, past <see cref="FileListing.EntryLimit"/>.</summary>
    public const int OutputLimit = 4 * 1024 * 1024;

    /// <summary>The script's exit code when it cannot enter the folder (missing, not a folder, no permission).</summary>
    public const int CannotEnter = 3;

    /// <summary>The script's exit codes when <c>find</c> could not read every entry: what it read is shown as incomplete.</summary>
    private const int EntriesUnread = 4;

    private const int LinksUnread = 5;

    private const string Script =
        "command -v find >/dev/null && command -v stat >/dev/null && command -v readlink >/dev/null || exit 127; " +
        "cd -- \"$1\" || exit 3; " +
        "find . -mindepth 1 -maxdepth 1 -exec stat -c '%f/%s/%Y/%n/' {} + || exit 4; " +
        "printf '\\0'; " +
        "find . -mindepth 1 -maxdepth 1 -type l -exec sh -c 'for l; do printf \"%s\\0\" \"$l\" \"$(readlink \"$l\")\"; done' sh {} + || exit 5";

    /// <summary>The exec's command: <c>sh -c &lt;script&gt; sh &lt;path&gt;</c>.</summary>
    public static IReadOnlyList<string> Command(ContainerPath path) => ["sh", "-c", Script, "sh", path.Value];

    /// <summary>
    /// The listing in the exec's output, or null when the run failed (could not enter the folder, missing tools, another exit code) or
    /// its output does not parse: then the archive listing is used.
    /// </summary>
    public static FileListing? Read(ContainerPath path, ExecOutput output)
    {
        var incomplete = output.ExitCode is EntriesUnread or LinksUnread;
        if (output.ExitCode is not (0 or null or EntriesUnread or LinksUnread))
            return null;

        ReadOnlySpan<byte> bytes = output.Output;
        var nul = bytes.IndexOf((byte)0);
        if (nul < 0 && !output.Truncated)
            return null;

        var records = nul < 0 ? bytes : bytes[..nul];
        var entries = ImmutableArray.CreateBuilder<FileEntry>();
        var capped = output.Truncated;
        while (!records.IsEmpty)
        {
            if (!TryRecord(ref records, out var entry))
            {
                // Output cut at the limit ends inside a record; anything else that does not parse is not this command's output.
                if (output.Truncated)
                    break;
                return null;
            }

            if (entries.Count == FileListing.EntryLimit)
            {
                capped = true;
                break;
            }

            entries.Add(entry);
        }

        if (nul >= 0 && !AddTargets(bytes[(nul + 1)..], entries))
            return null;

        return new FileListing(path, entries.ToImmutable(), ListingWay.Exec, incomplete, capped);
    }

    private static bool TryRecord(ref ReadOnlySpan<byte> records, out FileEntry entry)
    {
        entry = null!;
        var rest = records;
        if (!TryField(ref rest, out var hex) || !TryField(ref rest, out var size) || !TryField(ref rest, out var mtime)
            || !rest.StartsWith("./"u8))
        {
            return false;
        }

        rest = rest[2..];
        var slash = rest.IndexOf((byte)'/');
        if (slash <= 0 || slash + 1 >= rest.Length || rest[slash + 1] != (byte)'\n')
            return false;

        var name = Encoding.UTF8.GetString(rest[..slash]);
        if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var mode)
            || !long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || !long.TryParse(mtime, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var kind = (mode & 0xF000) switch
        {
            0x4000 => FileKind.Folder,
            0xA000 => FileKind.Link,
            0x8000 => FileKind.File,
            _ => FileKind.Other,
        };
        entry = new FileEntry(name, kind, kind == FileKind.File ? length : null, Time(seconds), ModeText.FromStatMode(mode), null);
        records = rest[(slash + 2)..];
        return true;
    }

    private static bool TryField(ref ReadOnlySpan<byte> rest, out string field)
    {
        var slash = rest.IndexOf((byte)'/');
        field = slash < 0 ? "" : Encoding.ASCII.GetString(rest[..slash]);
        if (slash <= 0)
            return false;
        rest = rest[(slash + 1)..];
        return true;
    }

    private static DateTimeOffset? Time(long seconds) =>
        seconds is >= -62_135_596_800 and <= 253_402_300_799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    /// <summary>Puts each link's target on its entry; false when the second part does not parse.</summary>
    private static bool AddTargets(ReadOnlySpan<byte> pairs, ImmutableArray<FileEntry>.Builder entries)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        while (!pairs.IsEmpty)
        {
            var end = pairs.IndexOf((byte)0);
            if (end < 0)
                break;
            var name = pairs[..end];
            pairs = pairs[(end + 1)..];
            end = pairs.IndexOf((byte)0);
            if (end < 0)
                break;
            if (!name.StartsWith("./"u8))
                return false;
            targets[Encoding.UTF8.GetString(name[2..])] = Encoding.UTF8.GetString(pairs[..end]);
            pairs = pairs[(end + 1)..];
        }

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Kind == FileKind.Link && targets.TryGetValue(entries[i].Name, out var target))
                entries[i] = entries[i] with { LinkTarget = target };
        }

        return true;
    }
}

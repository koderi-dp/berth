using System.Globalization;
using System.Text;

namespace Berth.Tests.Files;

/// <summary>Builds tar archives byte by byte, for entries no tool would write (sparse files, names that escape, devices).</summary>
internal sealed class TarBytes
{
    private readonly MemoryStream _bytes = new();

    /// <summary>A ustar header for <paramref name="name"/> of type <paramref name="type"/>, then <paramref name="data"/> padded to a block.</summary>
    public TarBytes Entry(string name, char type, byte[]? data = null, string link = "", long? sizeField = null, int mode = 420)
    {
        data ??= [];
        var header = new byte[512];
        Put(header, 0, 100, Encoding.UTF8.GetBytes(name));
        Octal(header, 100, 8, mode);
        Octal(header, 108, 8, 0);
        Octal(header, 116, 8, 0);
        Octal(header, 124, 12, sizeField ?? data.Length);
        Octal(header, 136, 12, 1_767_323_045);
        header[156] = (byte)type;
        Put(header, 157, 100, Encoding.UTF8.GetBytes(link));
        Put(header, 257, 6, "ustar\0"u8.ToArray());
        Put(header, 263, 2, "00"u8.ToArray());
        for (var i = 148; i < 156; i++)
            header[i] = (byte)' ';
        var sum = header.Sum(b => b);
        Octal(header, 148, 7, sum);
        header[155] = (byte)' ';
        _bytes.Write(header);
        Data(data);
        return this;
    }

    public TarBytes File(string name, string text) => Entry(name, '0', Encoding.UTF8.GetBytes(text));

    public TarBytes Folder(string name) => Entry(name.EndsWith('/') ? name : name + "/", '5', mode: 493);

    public TarBytes Symlink(string name, string target) => Entry(name, '2', link: target);

    /// <summary>A PAX extended header that applies to the next entry.</summary>
    public TarBytes Pax(params (string Key, string Value)[] records)
    {
        var body = new StringBuilder();
        foreach (var (key, value) in records)
        {
            var line = $" {key}={value}\n";
            var length = Encoding.UTF8.GetByteCount(line);
            // The record's length counts its own digits.
            var digits = 1;
            while ((length + digits).ToString(CultureInfo.InvariantCulture).Length != digits)
                digits++;
            body.Append((length + digits).ToString(CultureInfo.InvariantCulture)).Append(line);
        }

        return Entry("PaxHeaders/x", 'x', Encoding.UTF8.GetBytes(body.ToString()));
    }

    /// <summary>The two empty blocks that end an archive.</summary>
    public byte[] End()
    {
        _bytes.Write(new byte[1024]);
        return _bytes.ToArray();
    }

    /// <summary>The archive so far, without its end: as if the stream broke off.</summary>
    public byte[] Unfinished() => _bytes.ToArray();

    private void Data(byte[] data)
    {
        _bytes.Write(data);
        var pad = (512 - (data.Length % 512)) % 512;
        _bytes.Write(new byte[pad]);
    }

    private static void Put(byte[] header, int offset, int length, byte[] value) =>
        value.AsSpan(0, Math.Min(length, value.Length)).CopyTo(header.AsSpan(offset, length));

    private static void Octal(byte[] header, int offset, int length, long value)
    {
        var text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        Put(header, offset, length, Encoding.ASCII.GetBytes(text + "\0"));
    }
}

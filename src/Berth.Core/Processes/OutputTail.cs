namespace Berth.Processes;

/// <summary>
/// The last bytes a program wrote to one of its streams, at most a fixed number, so a program that writes without end cannot fill memory.
/// Bytes are dropped from the front in pairs, so UTF-16 text stays aligned. Safe to read while it is written.
/// </summary>
public sealed class OutputTail(int capacity)
{
    private readonly Lock _gate = new();
    private readonly List<byte> _bytes = [];

    public void Append(ReadOnlySpan<byte> chunk)
    {
        lock (_gate)
        {
            if (chunk.Length > capacity)
            {
                var drop = chunk.Length - capacity;
                chunk = chunk[Math.Min(chunk.Length, drop + drop % 2)..];
            }

            _bytes.AddRange(chunk);
            var over = _bytes.Count - capacity;
            if (over > 0)
                _bytes.RemoveRange(0, Math.Min(_bytes.Count, over + over % 2));
        }
    }

    public byte[] ToArray()
    {
        lock (_gate)
            return [.. _bytes];
    }
}

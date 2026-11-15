using System.Globalization;

namespace Berth.Engine;

/// <summary>A Docker Engine API version such as "1.56". Compares by major, then minor, as numbers.</summary>
public readonly record struct ApiVersion(int Major, int Minor) : IComparable<ApiVersion>
{
    /// <summary>The oldest API Berth talks to (Docker 25).</summary>
    public static readonly ApiVersion Minimum = new(1, 44);

    /// <summary>The API Berth is developed against; a newer engine is spoken to at this version.</summary>
    public static readonly ApiVersion Maximum = new(1, 56);

    /// <summary>Parses "major.minor", both non-negative whole numbers.</summary>
    public static bool TryParse(string text, out ApiVersion version)
    {
        version = default;
        var dot = text.IndexOf('.');
        if (dot < 0
            || !TryParsePart(text.AsSpan(0, dot), out var major)
            || !TryParsePart(text.AsSpan(dot + 1), out var minor))
        {
            return false;
        }

        version = new ApiVersion(major, minor);
        return true;
    }

    public int CompareTo(ApiVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");

    private static bool TryParsePart(ReadOnlySpan<char> part, out int value) =>
        int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

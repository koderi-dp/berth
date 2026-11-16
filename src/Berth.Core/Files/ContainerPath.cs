namespace Berth.Files;

/// <summary>
/// An absolute path inside a container: POSIX, forward slashes, no drive letters, separate from Windows paths. Always normalised: no
/// empty, <c>.</c> or <c>..</c> segment, no slash at the end, and never above <c>/</c> (<c>/..</c> is <c>/</c>). A name may hold
/// any character but <c>/</c> and NUL, spaces, backslashes and line breaks included. The default value is the root.
/// </summary>
public readonly struct ContainerPath : IEquatable<ContainerPath>
{
    // Null for the root, so default(ContainerPath) is the root.
    private readonly string? _value;

    private ContainerPath(string value) => _value = value == "/" ? null : value;

    public static ContainerPath Root => default;

    /// <summary>"/" or "/a/b".</summary>
    public string Value => _value ?? "/";

    public bool IsRoot => _value is null;

    /// <summary>The last segment; "/" for the root.</summary>
    public string Name => _value is null ? "/" : _value[(_value.LastIndexOf('/') + 1)..];

    /// <summary>The folder that holds this one; the root's parent is the root.</summary>
    public ContainerPath Parent
    {
        get
        {
            if (_value is null)
                return Root;
            var slash = _value.LastIndexOf('/');
            return slash <= 0 ? Root : new ContainerPath(_value[..slash]);
        }
    }

    /// <summary>The root first, then each folder down to this path, with the path each one stands for: the breadcrumb.</summary>
    public IReadOnlyList<(string Name, ContainerPath Path)> Segments
    {
        get
        {
            var segments = new List<(string, ContainerPath)> { ("/", Root) };
            if (_value is null)
                return segments;
            for (var i = 1; i <= _value.Length; i++)
            {
                if (i == _value.Length || _value[i] == '/')
                {
                    var path = new ContainerPath(_value[..i]);
                    segments.Add((path.Name, path));
                }
            }

            return segments;
        }
    }

    /// <summary>The entry <paramref name="name"/> inside this folder. A name that is empty, <c>.</c>, <c>..</c>, or holds <c>/</c> or NUL throws.</summary>
    public ContainerPath Join(string name)
    {
        if (!IsPlainName(name))
            throw new ArgumentException("A name is one segment: not empty, not \".\" or \"..\", without \"/\" or NUL.", nameof(name));
        return new ContainerPath(_value is null ? "/" + name : _value + "/" + name);
    }

    /// <summary>Whether <paramref name="name"/> can be one segment of a path.</summary>
    public static bool IsPlainName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\0']) < 0;

    /// <summary>
    /// Reads what the user typed: an absolute path, or one relative to <paramref name="from"/>. Empty text is <paramref name="from"/>.
    /// Line breaks at the end (a pasted line) are dropped; any other character is part of a name. False when the text holds a NUL.
    /// </summary>
    public static bool TryParse(string text, ContainerPath from, out ContainerPath path)
    {
        path = from;
        var trimmed = text.TrimEnd('\r', '\n');
        if (trimmed.Contains('\0'))
            return false;

        var segments = new List<string>();
        if (!trimmed.StartsWith('/') && from._value is { } start)
            segments.AddRange(start[1..].Split('/'));

        foreach (var segment in trimmed.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    break;
                case "..":
                    if (segments.Count > 0)
                        segments.RemoveAt(segments.Count - 1);
                    break;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        path = segments.Count == 0 ? Root : new ContainerPath("/" + string.Join('/', segments));
        return true;
    }

    public bool Equals(ContainerPath other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ContainerPath other && Equals(other);

    public override int GetHashCode() => _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    public override string ToString() => Value;

    public static bool operator ==(ContainerPath left, ContainerPath right) => left.Equals(right);

    public static bool operator !=(ContainerPath left, ContainerPath right) => !left.Equals(right);
}

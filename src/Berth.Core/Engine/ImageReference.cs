namespace Berth.Engine;

/// <summary>
/// An image reference split the way the docker CLI splits it for a pull: <see cref="Name"/> is the repository (with its registry
/// host and port, if any) and <see cref="Tag"/> is the tag, or the digest ("sha256:...") when the reference has one.
/// </summary>
public sealed record ImageReference(string Name, string Tag)
{
    /// <summary>
    /// Splits <paramref name="reference"/>: "nginx" is nginx:latest, "host:5000/a/b:tag" is host:5000/a/b and tag, and
    /// "a@sha256:..." (with or without a tag before the "@") is a and the digest. Surrounding spaces are ignored.
    /// </summary>
    /// <exception cref="ArgumentException">The reference is blank or has no name.</exception>
    public static ImageReference Parse(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var text = reference.Trim();

        var at = text.IndexOf('@', StringComparison.Ordinal);
        var (name, tag) = at >= 0 ? (SplitTag(text[..at]).Name, text[(at + 1)..]) : SplitTag(text);
        tag ??= "latest";

        if (name.Length == 0 || tag.Length == 0)
        {
            throw new ArgumentException($"\"{reference}\" is not an image reference.", nameof(reference));
        }

        return new ImageReference(name, tag);
    }

    /// <summary>The name and the tag after its last ":", or no tag when that ":" is a registry's port (a "/" follows it).</summary>
    private static (string Name, string? Tag) SplitTag(string text)
    {
        var colon = text.LastIndexOf(':');
        return colon > text.LastIndexOf('/') ? (text[..colon], text[(colon + 1)..]) : (text, null);
    }
}

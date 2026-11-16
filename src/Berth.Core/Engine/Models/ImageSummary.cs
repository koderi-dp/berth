namespace Berth.Engine.Models;

/// <summary>
/// One entry of the image list. <see cref="RepoTags"/> and <see cref="RepoDigests"/> are empty for an untagged (dangling) image;
/// the engine's "&lt;none&gt;:&lt;none&gt;" placeholders are dropped. <see cref="Created"/> is in Unix seconds.
/// <see cref="Containers"/> is how many containers use the image, or null when the engine did not count them.
/// </summary>
public sealed record ImageSummary(
    string Id,
    IReadOnlyList<string> RepoTags,
    IReadOnlyList<string> RepoDigests,
    long Created,
    long Size,
    int? Containers);

/// <summary>
/// The parts of an image's inspect answer Berth shows. <see cref="ExposedPorts"/> are as the engine names them ("80/tcp").
/// <see cref="Created"/> is null when the engine sent no readable time.
/// </summary>
public sealed record ImageDetails(
    string Id,
    IReadOnlyList<string> RepoTags,
    IReadOnlyList<string> RepoDigests,
    DateTimeOffset? Created,
    long Size,
    string Architecture,
    string Os,
    IReadOnlyList<string> ExposedPorts,
    IReadOnlyList<string> Env,
    IReadOnlyList<string> Cmd,
    IReadOnlyList<string> Entrypoint,
    string WorkingDir,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>One line of an image removal's answer: a tag that was removed, or an image (layer) that was deleted.</summary>
public sealed record ImageDeleteResult(string? Untagged, string? Deleted);

/// <summary>
/// One line of a pull's progress. <see cref="LayerId"/> is the layer the line is about, the tag for the first line, or empty for a
/// line about the whole pull (the digest and the final status). <see cref="Current"/> and <see cref="Total"/> are bytes, null
/// when the line carries no progress.
/// </summary>
public sealed record PullProgress(string LayerId, string Status, long? Current, long? Total);

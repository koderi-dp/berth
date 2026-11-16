using Berth.State;

namespace Berth.Presentation;

/// <summary>The pull dialog's field: what is wrong with the reference typed, and whether Pull can send it.</summary>
public static class PullReference
{
    public const string WhitespaceError = "An image name has no spaces.";

    public const string NameError = "Enter an image name, with an optional :tag or @digest.";

    /// <summary>
    /// The error under the field: null while nothing is typed or the reference is good; otherwise that it holds whitespace (anywhere,
    /// which the engine would refuse), or that it is no image name.
    /// </summary>
    public static string? Error(string typed) =>
        typed.Trim().Length == 0 ? null
        : typed.Any(char.IsWhiteSpace) ? WhitespaceError
        : ImagePull.KeyOf(typed) is null ? NameError
        : null;

    /// <summary>Whether Pull can send <paramref name="typed"/>: something is typed and <see cref="Error"/> has nothing to say.</summary>
    public static bool CanPull(string typed) => typed.Trim().Length > 0 && Error(typed) is null;
}

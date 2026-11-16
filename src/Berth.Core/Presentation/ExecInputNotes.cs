using System.Text;
using Berth.Session;

namespace Berth.Presentation;

/// <summary>A line under the terminal about input that did not reach the shell, for the shell of <paramref name="Generation"/>.</summary>
public sealed record ExecInputNote(int Generation, string Text);

/// <summary>
/// When the Exec tab says that input did not reach the shell. Input travels as whole pieces (a paste with its bracketed-paste markers
/// is one piece), and a piece that would take the bytes waiting for the shell past <see cref="ExecInput.QueueLimit"/> is dropped whole.
/// </summary>
public static class ExecInputNotes
{
    public const string DroppedText = "Input was dropped: the shell is not reading.";

    public const string PasteTooLargeText = "The clipboard holds more than 2 MB of text: it was not pasted.";

    // ESC [ 2 0 0 ~ before a bracketed paste and ESC [ 2 0 1 ~ after it.
    private const int BracketedPasteMarkerBytes = 12;

    /// <summary>
    /// The note after a piece of input for the shell of <paramref name="generation"/> came back with <paramref name="outcome"/>: a new
    /// one when it was dropped, otherwise <paramref name="current"/> (a note stays until it is dismissed or its shell stops running).
    /// </summary>
    public static ExecInputNote? AfterInput(ExecInputNote? current, ExecInputOutcome outcome, int generation) =>
        outcome == ExecInputOutcome.Dropped ? new ExecInputNote(generation, DroppedText) : current;

    /// <summary>Whether a paste of <paramref name="text"/> could be taken by a shell at all; a larger one is not sent.</summary>
    public static bool PasteFits(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encoding.UTF8.GetByteCount(text) + BracketedPasteMarkerBytes <= ExecInput.QueueLimit;
    }

    /// <summary>The note for a paste that was not sent because it can never fit.</summary>
    public static ExecInputNote PasteTooLarge(int generation) => new(generation, PasteTooLargeText);

    /// <summary>The note's text while its shell is the one running; null otherwise (or with no note).</summary>
    public static string? Shown(ExecInputNote? note, ExecState exec)
    {
        ArgumentNullException.ThrowIfNull(exec);
        return note is not null && exec.Status == ExecStatus.Running && exec.Generation == note.Generation ? note.Text : null;
    }
}

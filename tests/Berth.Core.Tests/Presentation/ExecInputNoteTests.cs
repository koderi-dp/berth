using Berth.Presentation;
using Berth.Session;

namespace Berth.Tests.Presentation;

public sealed class ExecInputNoteTests
{
    private static ExecState Exec(ExecStatus status, int generation) => ExecState.None with { Status = status, Generation = generation };

    [Fact]
    public void Input_dropped_for_a_shell_that_does_not_read_is_told()
    {
        var note = ExecInputNotes.AfterInput(null, ExecInputOutcome.Dropped, generation: 3);

        Assert.Equal(ExecInputNotes.DroppedText, ExecInputNotes.Shown(note, Exec(ExecStatus.Running, 3)));
        Assert.Equal("Input was dropped: the shell is not reading.", ExecInputNotes.DroppedText);
    }

    [Theory]
    [InlineData(ExecInputOutcome.Sent)]
    [InlineData(ExecInputOutcome.Ignored)]
    public void Input_that_was_taken_or_not_for_the_live_shell_says_nothing(ExecInputOutcome outcome)
    {
        Assert.Null(ExecInputNotes.AfterInput(null, outcome, generation: 3));
    }

    [Fact]
    public void A_note_stays_until_it_is_dismissed_even_when_later_input_is_taken()
    {
        var note = ExecInputNotes.AfterInput(null, ExecInputOutcome.Dropped, 3);

        Assert.Same(note, ExecInputNotes.AfterInput(note, ExecInputOutcome.Sent, 3));
    }

    [Theory]
    [InlineData(ExecStatus.Starting, 4)]
    [InlineData(ExecStatus.Running, 4)]
    [InlineData(ExecStatus.Ended, 3)]
    [InlineData(ExecStatus.Failed, 3)]
    [InlineData(ExecStatus.NotStarted, 3)]
    public void It_is_not_shown_once_its_shell_is_not_the_one_running(ExecStatus status, int generation)
    {
        var note = ExecInputNotes.AfterInput(null, ExecInputOutcome.Dropped, 3);

        Assert.Null(ExecInputNotes.Shown(note, Exec(status, generation)));
    }

    [Fact]
    public void A_paste_the_shell_could_never_take_is_not_sent_and_says_so()
    {
        // The paste goes out wrapped in the bracketed-paste markers (12 bytes), in UTF-8.
        var fits = new string('a', ExecInput.QueueLimit - 12);
        var tooLarge = fits + "a";
        var wide = new string('é', ExecInput.QueueLimit / 2); // two bytes each in UTF-8

        Assert.True(ExecInputNotes.PasteFits(fits));
        Assert.False(ExecInputNotes.PasteFits(tooLarge));
        Assert.False(ExecInputNotes.PasteFits(wide));
        var note = ExecInputNotes.PasteTooLarge(5);
        Assert.Equal(ExecInputNotes.PasteTooLargeText, ExecInputNotes.Shown(note, Exec(ExecStatus.Running, 5)));
    }
}

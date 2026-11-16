using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class TerminalScreenTests
{
    [Fact]
    public void Output_of_a_new_shell_starts_a_new_screen_then_shows_and_older_output_is_dropped()
    {
        var screen = new TerminalScreen();

        Assert.Equal(TerminalOutput.NewScreen, screen.Output(screen.Session, 1));
        Assert.Equal(TerminalOutput.Show, screen.Output(screen.Session, 1));
        Assert.Equal(TerminalOutput.NewScreen, screen.Output(screen.Session, 2));
        Assert.Equal(TerminalOutput.Drop, screen.Output(screen.Session, 1));
        Assert.Equal(2, screen.Generation);
    }

    [Fact]
    public void Begin_starts_a_new_screen_once_per_shell()
    {
        var screen = new TerminalScreen();

        Assert.True(screen.Begin(1));
        Assert.False(screen.Begin(1));
        Assert.Equal(TerminalOutput.Show, screen.Output(screen.Session, 1));
        Assert.False(screen.Begin(0));
    }

    [Fact]
    public void A_new_screen_starts_the_flow_count_again()
    {
        var screen = new TerminalScreen();
        screen.Output(screen.Session, 1);
        screen.Flow.Sent(TerminalFlow.High + 1);
        Assert.True(screen.Flow.Paused);

        Assert.Equal(TerminalOutput.NewScreen, screen.Output(screen.Session, 2));
        Assert.False(screen.Flow.Paused);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void The_first_shell_of_a_new_session_shows_its_output_on_a_cleared_screen_with_fresh_flow(int shellsBefore)
    {
        var screen = new TerminalScreen();
        for (var generation = 1; generation <= shellsBefore; generation++)
        {
            Assert.True(screen.Begin(generation));
            screen.Output(screen.Session, generation);
        }

        screen.Flow.Sent(TerminalFlow.High + 1);

        // Each detail session numbers its shells from 1 again.
        screen.NewSession();

        Assert.False(screen.Flow.Paused);
        Assert.True(screen.Begin(1));
        Assert.Equal(TerminalOutput.Show, screen.Output(screen.Session, 1));

        var other = new TerminalScreen();
        for (var generation = 1; generation <= shellsBefore; generation++)
            other.Output(other.Session, generation);
        other.NewSession();
        Assert.Equal(TerminalOutput.NewScreen, other.Output(other.Session, 1));
    }

    [Fact]
    public void Output_an_old_session_was_still_writing_after_a_new_session_began_is_dropped()
    {
        var screen = new TerminalScreen();
        var old = screen.Session;
        Assert.True(screen.Begin(1));
        Assert.True(screen.Begin(2));
        Assert.Equal(TerminalOutput.Show, screen.Output(old, 2));

        var current = screen.NewSession();

        // The old pump's write was already under way when the terminal followed the new session.
        Assert.NotEqual(old, current);
        Assert.Equal(TerminalOutput.Drop, screen.Output(old, 2));
        Assert.Equal(0, screen.Generation);
        Assert.Equal(TerminalOutput.NewScreen, screen.Output(current, 1));
        Assert.Equal(TerminalOutput.Drop, screen.Output(old, 3));
        Assert.Equal(TerminalOutput.Show, screen.Output(current, 1));
    }
}

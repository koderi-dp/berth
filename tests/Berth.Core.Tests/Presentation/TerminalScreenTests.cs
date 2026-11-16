using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class TerminalScreenTests
{
    [Fact]
    public void Output_of_a_new_shell_starts_a_new_screen_then_shows_and_older_output_is_dropped()
    {
        var screen = new TerminalScreen();

        Assert.Equal(TerminalOutput.NewScreen, screen.Output(1));
        Assert.Equal(TerminalOutput.Show, screen.Output(1));
        Assert.Equal(TerminalOutput.NewScreen, screen.Output(2));
        Assert.Equal(TerminalOutput.Drop, screen.Output(1));
        Assert.Equal(2, screen.Generation);
    }

    [Fact]
    public void Begin_starts_a_new_screen_once_per_shell()
    {
        var screen = new TerminalScreen();

        Assert.True(screen.Begin(1));
        Assert.False(screen.Begin(1));
        Assert.Equal(TerminalOutput.Show, screen.Output(1));
        Assert.False(screen.Begin(0));
    }

    [Fact]
    public void A_new_screen_starts_the_flow_count_again()
    {
        var screen = new TerminalScreen();
        screen.Output(1);
        screen.Flow.Sent(TerminalFlow.High + 1);
        Assert.True(screen.Flow.Paused);

        Assert.Equal(TerminalOutput.NewScreen, screen.Output(2));
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
            screen.Output(generation);
        }

        screen.Flow.Sent(TerminalFlow.High + 1);

        // Each detail session numbers its shells from 1 again.
        screen.NewSession();

        Assert.False(screen.Flow.Paused);
        Assert.True(screen.Begin(1));
        Assert.Equal(TerminalOutput.Show, screen.Output(1));

        var other = new TerminalScreen();
        for (var generation = 1; generation <= shellsBefore; generation++)
            other.Output(generation);
        other.NewSession();
        Assert.Equal(TerminalOutput.NewScreen, other.Output(1));
    }
}

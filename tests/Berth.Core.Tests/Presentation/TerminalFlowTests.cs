using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class TerminalFlowTests
{
    [Fact]
    public void Pauses_above_high_and_resumes_below_low()
    {
        var flow = new TerminalFlow();

        flow.Sent(TerminalFlow.High);
        Assert.False(flow.Paused);
        flow.Sent(1);
        Assert.True(flow.Paused);

        flow.Acked(TerminalFlow.High + 1 - TerminalFlow.Low + 1);
        Assert.False(flow.Paused);
    }

    [Fact]
    public void Stays_paused_between_low_and_high()
    {
        var flow = new TerminalFlow();
        flow.Sent(TerminalFlow.High + 1);

        flow.Acked(TerminalFlow.High + 1 - TerminalFlow.Low);
        Assert.True(flow.Paused);

        flow.Sent(1000);
        flow.Acked(1000);
        Assert.True(flow.Paused);
    }

    [Fact]
    public void An_ack_larger_than_what_was_sent_stops_at_zero()
    {
        var flow = new TerminalFlow();
        flow.Sent(100);

        flow.Acked(1_000_000);
        flow.Sent(TerminalFlow.High);

        Assert.False(flow.Paused);
        flow.Sent(1);
        Assert.True(flow.Paused);
    }

    [Fact]
    public void Reset_clears_it()
    {
        var flow = new TerminalFlow();
        flow.Sent(TerminalFlow.High * 2);

        flow.Reset();

        Assert.False(flow.Paused);
        flow.Sent(TerminalFlow.High);
        Assert.False(flow.Paused);
    }
}

using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class TerminalRecoveryTests
{
    [Theory]
    [InlineData(TerminalProcessFailure.BrowserExited, TerminalRecoveryStep.Recreate)]
    [InlineData(TerminalProcessFailure.RendererExited, TerminalRecoveryStep.Reload)]
    [InlineData(TerminalProcessFailure.RendererUnresponsive, TerminalRecoveryStep.Ignore)]
    [InlineData(TerminalProcessFailure.HelperExited, TerminalRecoveryStep.Ignore)]
    public void Each_failed_process_has_its_step(TerminalProcessFailure failure, TerminalRecoveryStep step)
    {
        Assert.Equal(step, TerminalRecovery.For(failure));
    }

    [Theory]
    [InlineData(TerminalRecoveryStep.Ignore, false)]
    [InlineData(TerminalRecoveryStep.Reload, true)]
    [InlineData(TerminalRecoveryStep.Recreate, true)]
    public void Only_a_reload_or_a_new_webview_ends_the_shell(TerminalRecoveryStep step, bool ends)
    {
        Assert.Equal(ends, TerminalRecovery.EndsTheShell(step));
    }
}

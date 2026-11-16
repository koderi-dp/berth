namespace Berth.Session;

/// <summary>The runtime of the connection's engine control commands.</summary>
public sealed partial class EngineSession
{
    private const string EngineCommandFailedReport = "an engine command could not be run";

    private readonly IEngineControl? _engineControl;

    /// <summary>
    /// Runs one command off the loop under <see cref="SessionReducer.EngineCommandTimeout"/> only. The command belongs to the
    /// connection it was started for and runs to its end or its limit even when this session is disposed (a switch of connection,
    /// an edit, the app quitting): half a start or half a stop is worse than a whole one. A disposed session drops the result
    /// (its loop has ended, so the post goes nowhere), so it never reaches the connection that replaced it. Nothing waits for it.
    /// The output goes to the state only.
    /// </summary>
    private void StartEngineCommand(EngineCommand command)
    {
        if (_engineControl is not { } control)
            return;
        _ = Task.Run(() => RunEngineCommandAsync(control, command));
    }

    private async Task RunEngineCommandAsync(IEngineControl control, EngineCommand command)
    {
        using var timeout = new CancellationTokenSource(SessionReducer.EngineCommandTimeout, _time);
        SessionEvent result;
        try
        {
            var outcome = await control.RunAsync(command, timeout.Token).ConfigureAwait(false);
            result = new EngineCommandExited(command, outcome.ExitCode, outcome.Output);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            result = new EngineCommandTimedOut(command);
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            Report(EngineCommandFailedReport, ex);
            result = new EngineCommandNotStarted(command, ex.Message);
        }

        Post(result, source: null);
    }
}

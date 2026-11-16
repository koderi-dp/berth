namespace Berth.Session;

/// <summary>The runtime of the connection's engine control commands.</summary>
public sealed partial class EngineSession
{
    private const string EngineCommandFailedReport = "an engine command could not be run";

    private readonly IEngineControl? _engineControl;

    // The command running now, if any; awaited by DisposeAsync. Written only by the loop.
    private Task? _engineCommand;

    /// <summary>
    /// Runs one command off the loop, under <see cref="SessionReducer.EngineCommandTimeout"/> and the session's lifetime: disposing the
    /// session ends it. Not tied to a connection: a stop outlives the connection it ends. The output goes to the state only.
    /// </summary>
    private void StartEngineCommand(EngineCommand command)
    {
        if (_engineControl is not { } control)
            return;
        _engineCommand = Task.Run(() => RunEngineCommandAsync(control, command));
    }

    private async Task RunEngineCommandAsync(IEngineControl control, EngineCommand command)
    {
        using var timeout = new CancellationTokenSource(SessionReducer.EngineCommandTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _lifetime.Token);
        SessionEvent result;
        try
        {
            var outcome = await control.RunAsync(command, linked.Token).ConfigureAwait(false);
            result = new EngineCommandExited(command, outcome.ExitCode, outcome.Output);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            result = new EngineCommandTimedOut(command);
        }
        catch (Exception ex)
        {
            Report(EngineCommandFailedReport, ex);
            result = new EngineCommandNotStarted(command, ex.Message);
        }

        Post(result, source: null);
    }

    /// <summary>Waits for a command still running when the session is disposed (its lifetime ended it). Never throws.</summary>
    private async Task EndEngineCommandAsync()
    {
        if (_engineCommand is not { } running)
            return;
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // It reports its own failures as events.
        }
    }
}

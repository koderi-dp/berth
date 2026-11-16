using Berth.Connections;
using Berth.Processes;
using Berth.Session;

namespace Berth;

/// <summary>
/// Runs a connection's engine control commands for its session (<see cref="CommandRunner"/>: no shell, no window, output captured
/// and bounded, the process tree ended on cancel), off the UI thread. The log gets fixed wording and the exit code only: never the
/// command line (it may hold anything the user typed) and never its output.
/// </summary>
internal sealed class EngineCommands(EngineControl control) : IEngineControl
{
    public async Task<EngineCommandResult> RunAsync(EngineCommand command, CancellationToken cancellationToken)
    {
        var name = command == EngineCommand.Start ? "start" : "stop";
        var parsed = CommandLine.Parse(command == EngineCommand.Start ? control.StartCommand : control.StopCommand);
        // The editor and the settings file refuse such a line; a check here keeps a bad one from running half understood.
        if (parsed.Error is { } error)
            throw new InvalidOperationException(error);

        AppLog.Write($"engine {name} command started");
        try
        {
            var outcome = await CommandRunner.RunAsync(parsed.Program, parsed.Arguments, cancellationToken).ConfigureAwait(false);
            AppLog.Write($"engine {name} command exited with code {outcome.ExitCode}");
            return new EngineCommandResult(outcome.ExitCode, outcome.Output);
        }
        catch (OperationCanceledException)
        {
            AppLog.Write($"engine {name} command was ended before it finished");
            throw;
        }
    }
}

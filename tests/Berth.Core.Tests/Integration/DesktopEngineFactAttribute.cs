namespace Berth.Tests.Integration;

/// <summary>
/// A test that may only run against Docker Desktop's engine (its named pipe), where everything else is named <c>berth-ref…</c>: skipped
/// unless <c>BERTH_ENGINE</c> holds that pipe's address.
/// </summary>
public sealed class DesktopEngineFactAttribute : FactAttribute
{
    public const string PipeName = "dockerDesktopLinuxEngine";

    public DesktopEngineFactAttribute()
    {
        var address = Environment.GetEnvironmentVariable(EngineFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(address) || !address.Contains(PipeName, StringComparison.OrdinalIgnoreCase))
            Skip = $"Set {EngineFactAttribute.Variable} to Docker Desktop's pipe (npipe:////./pipe/{PipeName}) to run this test.";
    }
}

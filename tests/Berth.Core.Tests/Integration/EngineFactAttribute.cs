namespace Berth.Tests.Integration;

/// <summary>A test that needs a real engine: skipped unless <c>BERTH_ENGINE</c> holds an engine address.</summary>
public sealed class EngineFactAttribute : FactAttribute
{
    public const string Variable = "BERTH_ENGINE";

    public EngineFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to an engine address to run this test.";
    }
}

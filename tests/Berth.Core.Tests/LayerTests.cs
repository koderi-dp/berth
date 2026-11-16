using System.Text.RegularExpressions;

namespace Berth.Tests;

/// <summary>
/// Core's layers depend one way (see the layout section of <c>AGENTS.md</c>). One assembly gives no compiler check, so these
/// tests read the source files.
/// </summary>
public sealed class LayerTests
{
    [Theory]
    [InlineData("Engine", "Presentation")]
    [InlineData("Engine", "Session")]
    [InlineData("Engine", "State")]
    [InlineData("Engine", "Settings")]
    [InlineData("Session", "Presentation")]
    [InlineData("State", "Presentation")]
    [InlineData("State", "Session")]
    public void A_layer_does_not_reference_a_layer_above_it(string folder, string forbidden)
    {
        // The full name anywhere, or the short name used as a qualifier (code under namespace Berth.X can write Presentation.Y).
        var reference = new Regex($@"\bBerth\.{forbidden}\b|(?<![\w.]){forbidden}\.[A-Z]");
        var root = Path.Combine(RepositoryRoot(), "src", "Berth.Core", folder);

        var offenders = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(file => reference.IsMatch(File.ReadAllText(file)))
                .Select(file => Path.GetRelativePath(root, file))
                .ToList()
            : [];

        Assert.True(Directory.Exists(root), $"src/Berth.Core/{folder} not found");
        Assert.Empty(offenders);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Berth.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Berth.slnx not found above " + AppContext.BaseDirectory);
    }
}

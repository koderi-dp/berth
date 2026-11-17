using System.Text.RegularExpressions;

namespace Berth.Tests;

/// <summary>
/// Core's folders depend one way (the table in the layout section of <c>AGENTS.md</c>, row for row with <see cref="Layers"/>). One
/// assembly gives no compiler check, so these tests read the source files.
/// </summary>
public sealed partial class LayerTests
{
    /// <summary>Each folder of <c>src/Berth.Core</c> and the folders it may reference; every other folder is out of bounds.</summary>
    public static TheoryData<string, string[]> Layers => new()
    {
        { "Connections", [] },
        { "Transport", ["Connections"] },
        { "Engine", ["Connections", "Transport"] },
        { "Text", ["Connections", "Engine"] },
        { "State", ["Engine"] },
        { "Files", ["Engine"] },
        { "Session", ["Connections", "Engine", "State", "Text", "Files"] },
        { "Presentation", ["Connections", "Engine", "State", "Session", "Text", "Files"] },
        { "Settings", ["Connections", "State", "Storage"] },
        { "Logging", [] },
        { "Storage", [] },
        { "Processes", [] },
    };

    private static readonly string[] Folders = [.. Layers.Select(row => (string)row[0])];

    [Theory]
    [MemberData(nameof(Layers))]
    public void A_folder_references_only_the_folders_it_may_use(string folder, string[] allowed)
    {
        var offenders = new List<string>();
        foreach (var other in Folders.Except([folder, .. allowed]))
        {
            var reference = Reference(other);
            offenders.AddRange(
                SourceFiles(folder)
                    .Where(file => reference.IsMatch(File.ReadAllText(file)))
                    .Select(file => $"{Path.GetRelativePath(CoreRoot, file)} uses Berth.{other}"));
        }

        Assert.Empty(offenders);
    }

    /// <summary>SSH.NET stays behind <c>IEngineTransport</c>: only the SSH transport's own files (<c>Transport/Ssh*.cs</c>) name it.</summary>
    [Fact]
    public void Only_the_ssh_transport_uses_ssh_net()
    {
        var sshNet = SshNetReference();
        var sources = Directory.EnumerateFiles(CoreRoot, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "Berth.App"), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file));
        var offenders = sources
            .Where(file => !IsSshTransport(file) && sshNet.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(RepositoryRoot(), file));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Ssh_net_is_referenced_by_core_alone()
    {
        var app = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Berth.App", "Berth.App.csproj"));
        var core = File.ReadAllText(Path.Combine(CoreRoot, "Berth.Core.csproj"));

        Assert.DoesNotContain("SSH.NET", app, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Include=\"SSH.NET\"", core, StringComparison.Ordinal);
    }

    private static bool IsSshTransport(string file) =>
        Path.GetDirectoryName(file) == Path.Combine(CoreRoot, "Transport") && Path.GetFileName(file).StartsWith("Ssh", StringComparison.Ordinal);

    private static bool IsBuildOutput(string file) =>
        file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");

    [GeneratedRegex(@"\bRenci\.SshNet\b")]
    private static partial Regex SshNetReference();

    [Fact]
    public void Every_folder_of_core_is_in_the_table()
    {
        var folders = Directory.EnumerateDirectories(CoreRoot)
            .Select(Path.GetFileName)
            .Where(name => name is not ("bin" or "obj"))
            .Order();

        Assert.Equal(Folders.Order(), folders);
    }

    [Fact]
    public void Every_file_is_in_the_namespace_of_its_folder()
    {
        var wrong = Folders
            .SelectMany(SourceFiles)
            .Where(file =>
            {
                var folder = Path.GetRelativePath(CoreRoot, Path.GetDirectoryName(file)!).Replace(Path.DirectorySeparatorChar, '.');
                return NamespaceLine().Match(File.ReadAllText(file)).Groups[1].Value != "Berth." + folder;
            })
            .Select(file => Path.GetRelativePath(CoreRoot, file));

        Assert.Empty(wrong);
    }

    private static string CoreRoot { get; } = Path.Combine(RepositoryRoot(), "src", "Berth.Core");

    // A folder that does not exist has no files here; Every_folder_of_core_is_in_the_table reports it.
    private static IEnumerable<string> SourceFiles(string folder) =>
        Directory.Exists(Path.Combine(CoreRoot, folder))
            ? Directory.EnumerateFiles(Path.Combine(CoreRoot, folder), "*.cs", SearchOption.AllDirectories)
            : [];

    /// <summary>
    /// A reference to namespace <c>Berth.{folder}</c>: its full name, or its short name followed by one of its types or child
    /// namespaces (code in another <c>Berth.*</c> namespace can write <c>Presentation.LogView</c>). The short name alone is not
    /// enough: <c>State.Running</c> is an ordinary property access.
    /// </summary>
    private static Regex Reference(string folder)
    {
        var root = Path.Combine(CoreRoot, folder);
        var names = SourceFiles(folder)
            .SelectMany(file => TypeDeclaration().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .Concat(Directory.Exists(root) ? Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Select(dir => new DirectoryInfo(dir).Name) : [])
            .Distinct()
            .Select(Regex.Escape);
        return new Regex($@"\bBerth\.{folder}\b|(?<![\w.]){folder}\.(?:{string.Join('|', names)})\b");
    }

    [GeneratedRegex(@"\b(?:class|record|struct|enum|interface)\s+([A-Z]\w*)")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"^namespace\s+([\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex NamespaceLine();

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

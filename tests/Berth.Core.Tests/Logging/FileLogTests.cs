using Berth.Logging;

namespace Berth.Tests.Logging;

public sealed class FileLogTests : IDisposable
{
    private const int OneMegabyte = 1024 * 1024;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "berth-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string LogFile => Path.Combine(_directory, "test.log");

    private string OlderFile => Path.Combine(_directory, "test.1.log");

    [Fact]
    public void Writes_a_line()
    {
        var log = new FileLog(_directory, "test.log");

        log.Write("started");

        var line = Assert.Single(File.ReadAllLines(LogFile));
        Assert.EndsWith(" started", line);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z started$", line);
    }

    [Fact]
    public void Rolls_at_one_megabyte_keeping_one_older_file()
    {
        var log = new FileLog(_directory, "test.log");
        var filler = new string('x', 10_000);

        // About 2.5 MB in all: enough to roll twice, so the second roll must replace the first older file.
        for (var i = 0; i < 250; i++)
            log.Write(i + " " + filler);
        log.Write("last line");

        Assert.Equal(["test.1.log", "test.log"], Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(new FileInfo(LogFile).Length <= OneMegabyte);
        Assert.True(new FileInfo(OlderFile).Length <= OneMegabyte);
        Assert.True(new FileInfo(OlderFile).Length > OneMegabyte - 2 * filler.Length, "the older file holds a full megabyte, less one line");
        Assert.EndsWith("last line", File.ReadAllLines(LogFile)[^1]);
        Assert.DoesNotContain(File.ReadAllLines(OlderFile), line => line.Contains(" 0 " + filler));
    }

    [Fact]
    public void A_locked_file_does_not_throw()
    {
        Directory.CreateDirectory(_directory);
        var log = new FileLog(_directory, "test.log");

        using (new FileStream(LogFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            log.Write("dropped while locked");
        }

        log.Write("written after the lock");

        var line = Assert.Single(File.ReadAllLines(LogFile));
        Assert.EndsWith(" written after the lock", line);
    }

    [Fact]
    public void Describe_leaves_out_the_message()
    {
        var ex = new IOException("tcp://10.0.0.5:2375 refused", new InvalidOperationException("secret-container-name"));

        var text = FileLog.Describe(ex);

        Assert.Contains("System.IO.IOException", text);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.DoesNotContain("10.0.0.5", text);
        Assert.DoesNotContain("refused", text);
        Assert.DoesNotContain("secret-container-name", text);
    }
}

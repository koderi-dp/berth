using System.Security.Cryptography;
using Berth.Engine;
using Berth.Files;

namespace Berth.Tests.Integration;

/// <summary>
/// The Files tab's engine calls against the engine at <c>BERTH_ENGINE</c>, in a throwaway <c>berth-it-…</c> container (alpine, so
/// busybox): both listings of a folder with awkward names, a download and an upload through the archive calls, and the engine's
/// messages for a path that is not there.
/// </summary>
public sealed partial class EngineIntegrationTests
{
    /// <summary>Makes /tmp/t with a space, a newline, unicode, a leading dash, a folder, a link to a file and a link to a folder.</summary>
    private const string AwkwardFolder =
        "mkdir -p '/tmp/t/sub dir' && cd /tmp/t && printf 'hello' > 'a b' && touch \"$(printf 'new\\nline')\" -- -dash 'ünï' "
        + "&& ln -s /etc/hosts link && ln -s /etc etclink && printf 'deep' > 'sub dir/deep.txt'";

    private static readonly string[] AwkwardNames = ["-dash", "a b", "etclink", "link", "new\nline", "sub dir", "ünï"];

    [EngineFact]
    public async Task A_folder_with_awkward_names_lists_the_same_through_exec_and_through_the_archive()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await engine.Cli.RunAsync("exec", name, "sh", "-c", AwkwardFolder);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);
            var path = ContainerPath.Root.Join("tmp").Join("t");

            var output = await client.RunExecForOutputAsync(id, ExecListing.Command(path), ExecListing.OutputLimit, cts.Token);
            var throughExec = ExecListing.Read(path, output);
            FileListing throughArchive;
            await using (var archive = await client.GetArchiveAsync(id, path.Value, cts.Token))
                throughArchive = await ArchiveListing.ReadAsync(path, archive.Content, ArchiveListing.Budget.Default, cts.Token);

            Assert.NotNull(throughExec);
            Assert.Equal(AwkwardNames, throughExec.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
            Assert.Equal(AwkwardNames, throughArchive.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
            Assert.Equal("/etc/hosts", throughExec.Entries.Single(e => e.Name == "link").LinkTarget);
            Assert.Equal("/etc/hosts", throughArchive.Entries.Single(e => e.Name == "link").LinkTarget);
            Assert.Equal(5, throughExec.Entries.Single(e => e.Name == "a b").Size);
            Assert.Equal(5, throughArchive.Entries.Single(e => e.Name == "a b").Size);
            var execModes = throughExec.Entries.ToDictionary(e => e.Name, e => e.Mode);
            Assert.All(throughArchive.Entries, e => Assert.Equal(execModes[e.Name], e.Mode));
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task A_folder_that_cannot_be_entered_fails_the_exec_listing_and_the_archive_says_why()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);
            var missing = ContainerPath.Root.Join("nope");

            var output = await client.RunExecForOutputAsync(id, ExecListing.Command(missing), ExecListing.OutputLimit, cts.Token);
            var error = await Assert.ThrowsAsync<EngineException>(() => client.GetArchiveAsync(id, missing.Value, cts.Token));
            var stat = await Assert.ThrowsAsync<EngineException>(() => client.StatPathAsync(id, missing.Value, cts.Token));

            Assert.Equal(ExecListing.CannotEnter, output.ExitCode);
            Assert.Null(ExecListing.Read(missing, output));
            Assert.Equal(404, error.StatusCode);
            Assert.Contains("/nope", error.Message, StringComparison.Ordinal);
            Assert.Equal(404, stat.StatusCode);
            _output.WriteLine($"missing path: {error.Message}");
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task A_link_to_a_folder_is_read_as_the_folder_with_a_trailing_dot_and_a_stopped_container_still_answers()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await engine.Cli.RunAsync("exec", name, "sh", "-c", AwkwardFolder);
            await engine.Cli.RunAsync("stop", name);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);
            var link = ContainerPath.Root.Join("tmp").Join("t").Join("etclink");

            var stat = await client.StatPathAsync(id, link.Value, cts.Token);
            FileListing listing;
            await using (var archive = await client.GetArchiveAsync(id, link.Value + "/.", cts.Token))
                listing = await ArchiveListing.ReadAsync(link, archive.Content, ArchiveListing.Budget.Default, cts.Token);

            Assert.True(stat.IsLink);
            Assert.Equal("/etc", stat.LinkTarget);
            Assert.Contains(listing.Entries, e => e.Name == "hosts");
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }

    [EngineFact]
    public async Task A_folder_downloads_whole_and_an_upload_lands_byte_for_byte()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        var local = Path.Combine(Path.GetTempPath(), "berth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await engine.Cli.RunAsync("exec", name, "sh", "-c", AwkwardFolder);
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);
            var t = ContainerPath.Root.Join("tmp").Join("t");

            var downloaded = await ArchiveTransfer.DownloadAsync(client, id, [t], local, replace: false, _ => { }, _ => { }, cts.Token);

            Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(local, "t", "a b")));
            Assert.Equal("deep", await File.ReadAllTextAsync(Path.Combine(local, "t", "sub dir", "deep.txt")));
            Assert.True(File.Exists(Path.Combine(local, "t", "new_line")));
            Assert.Equal(2, downloaded.Links);
            Assert.Equal(1, downloaded.Renamed);

            var payload = new byte[300_000];
            Random.Shared.NextBytes(payload);
            var source = Path.Combine(local, "upload.bin");
            await File.WriteAllBytesAsync(source, payload);
            var uploaded = await ArchiveTransfer.UploadAsync(client, id, [source], t, [], _ => { }, _ => { }, cts.Token);
            var hash = await engine.Cli.RunAsync("exec", name, "sha256sum", "/tmp/t/upload.bin");

            Assert.Equal(1, uploaded.Files);
            Assert.StartsWith(Convert.ToHexStringLower(SHA256.HashData(payload)), hash, StringComparison.Ordinal);

            var refused = await Assert.ThrowsAsync<EngineException>(() =>
                ArchiveTransfer.UploadAsync(client, id, [source], ContainerPath.Root.Join("nope"), [], _ => { }, _ => { }, cts.Token));
            _output.WriteLine($"upload into a missing folder: {refused.StatusCode} {refused.Message}");
            Assert.NotEqual(0, refused.StatusCode);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
            Directory.Delete(local, recursive: true);
        }
    }

    [EngineFact]
    public async Task The_exec_listing_reads_a_large_folder_up_to_its_limit()
    {
        await using var engine = TestEngine.FromEnvironment();
        var name = DockerCli.NewContainerName();
        try
        {
            var id = await engine.Cli.RunAsync(name);
            await engine.Cli.RunAsync("exec", name, "sh", "-c", "mkdir /tmp/many && cd /tmp/many && i=0; while [ $i -lt 10050 ]; do : > f$i; i=$((i+1)); done");
            await using var client = engine.CreateClient();
            using var cts = new CancellationTokenSource(Patience);
            await client.ConnectAsync(cts.Token);
            var many = ContainerPath.Root.Join("tmp").Join("many");

            var listing = ExecListing.Read(many, await client.RunExecForOutputAsync(id, ExecListing.Command(many), ExecListing.OutputLimit, cts.Token));

            Assert.NotNull(listing);
            Assert.Equal(FileListing.EntryLimit, listing.Entries.Length);
            Assert.True(listing.Capped);
        }
        finally
        {
            await engine.Cli.RemoveAsync(name);
        }
    }
}


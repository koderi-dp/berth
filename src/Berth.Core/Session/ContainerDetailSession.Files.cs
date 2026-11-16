using Berth.Engine;
using Berth.Files;

namespace Berth.Session;

/// <summary>
/// The Files tab's listing: runs the exec or the archive listing the reducer asks for and posts what came of it. A failed exec listing
/// is not reported (the archive listing follows); a failed archive listing is, in fixed words. Paths are never logged.
/// </summary>
public sealed partial class ContainerDetailSession
{
    private const string ListingFailedReport = "listing a folder failed";

    /// <summary>Shows the folder <paramref name="path"/> on the Files tab.</summary>
    public void BrowseFiles(ContainerPath path) => Post(new FilesBrowseRequested(path));

    /// <summary>Reads the folder shown on the Files tab again (the root the first time).</summary>
    public void RefreshFiles() => Post(new FilesRefreshRequested());

    private void StartListing(ListFilesEffect list) =>
        Run(ct => list.Way == ListingWay.Exec ? ListThroughExecAsync(list, ct) : ListThroughArchiveAsync(list, ct), _calls.Token);

    private async Task ListThroughExecAsync(ListFilesEffect list, CancellationToken ct)
    {
        FilesEvent result;
        try
        {
            var output = await _api.RunExecForOutputAsync(State.ContainerId, ExecListing.Command(list.Path), ExecListing.OutputLimit, ct)
                .ConfigureAwait(false);
            result = ExecListing.Read(list.Path, output) is { } listing
                ? new FilesListed(list.Generation, listing)
                : new ExecListingFailed(list.Generation, output.ExitCode is { } code ? ExecListingFailure.Exited(code) : ExecListingFailure.Unreadable);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (EngineException ex)
        {
            result = new ExecListingFailed(list.Generation, ExecListingFailure.Refused(ex.StatusCode));
        }
        catch (Exception)
        {
            result = new ExecListingFailed(list.Generation, ExecListingFailure.Unreadable);
        }

        Post(result);
    }

    /// <summary>
    /// Lists through the engine's archive of the folder. A path that is a link is read again as "path/.", which the engine resolves, so
    /// a link to a folder lists that folder; a path that is a file is not a folder, and its content is not read.
    /// </summary>
    private async Task ListThroughArchiveAsync(ListFilesEffect list, CancellationToken ct)
    {
        FilesEvent result;
        try
        {
            var archive = await _api.GetArchiveAsync(State.ContainerId, list.Path.Value, ct).ConfigureAwait(false);
            if (archive.Stat is { IsLink: true })
            {
                await archive.DisposeAsync().ConfigureAwait(false);
                archive = await _api.GetArchiveAsync(State.ContainerId, list.Path.IsRoot ? "/." : list.Path.Value + "/.", ct).ConfigureAwait(false);
            }

            await using (archive)
            {
                if (archive.Stat is { IsDirectory: false, IsLink: false })
                    throw new NotAFolderException(list.Path);
                var listing = await ArchiveListing.ReadAsync(list.Path, archive.Content, ArchiveListing.Budget.Default, ct, _time).ConfigureAwait(false);
                result = new FilesListed(list.Generation, listing);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (NotAFolderException ex)
        {
            result = new FilesListFailed(list.Generation, ex.Message, NotAFolder: true);
        }
        catch (Exception ex)
        {
            if (ex is not EngineException { StatusCode: 404 })
                Report(ListingFailedReport, ex);
            result = new FilesListFailed(list.Generation, ex.Message, NotAFolder: false);
        }

        Post(result);
    }
}

using System.Collections.Immutable;
using Berth.Files;

namespace Berth.Session;

/// <summary>The Files tab's listing: which way a folder is read (<see cref="ListingChoice"/>), the fall-back, and which answers count.</summary>
public static partial class DetailReducer
{
    private static (DetailState, ImmutableArray<DetailEffect>) ReduceFiles(DetailState state, FilesEvent e)
    {
        var files = state.Files;
        var current = !state.ConnectionLost;
        switch (e)
        {
            case FilesBrowseRequested browse when CanList(state):
                return List(state, browse.Path);

            case FilesRefreshRequested when CanList(state):
                return List(state, files.Path);

            case FilesListed listed when current && IsAwaited(files, listed.Generation):
                return (state with
                {
                    Files = files with { Status = FilesStatus.Loaded, Listing = listed.Listing, Way = listed.Listing.Way, Error = null, NotAFolder = false },
                }, None);

            case ExecListingFailed failed when current && IsAwaited(files, failed.Generation) && files.Way == ListingWay.Exec:
            {
                var next = files with
                {
                    Way = ListingWay.Archive,
                    ExecUnavailable = files.ExecUnavailable || ListingChoice.ExecUnavailableAfter(failed.Failure),
                };
                return (state with { Files = next }, [new ListFilesEffect(files.Generation, files.Path, ListingWay.Archive)]);
            }

            case FilesListFailed failed when current && IsAwaited(files, failed.Generation):
                return (state with
                {
                    Files = files with { Status = FilesStatus.Failed, Listing = null, Error = failed.Message, NotAFolder = failed.NotAFolder },
                }, None);

            default:
                return (state, None);
        }
    }

    /// <summary>A folder is read once the container has been read, while it exists, on a live connection.</summary>
    private static bool CanList(DetailState state) => !state.ConnectionLost && !state.Removed && state.Details is not null;

    private static bool IsAwaited(FilesState files, int generation) => files.Status == FilesStatus.Loading && files.Generation == generation;

    /// <summary>Reads <paramref name="path"/>: through exec where it works (<see cref="ListingChoice.First"/>), from the archive otherwise.</summary>
    private static (DetailState, ImmutableArray<DetailEffect>) List(DetailState state, ContainerPath path)
    {
        var files = state.Files;
        var running = state.Details?.State is { Running: true, Paused: false, Restarting: false };
        var way = ListingChoice.First(running, files.ExecUnavailable);
        var generation = files.Generation + 1;
        var next = files with
        {
            Path = path,
            Status = FilesStatus.Loading,
            // The rows stay while the same folder is read again, so a refresh does not empty the table.
            Listing = files.Path == path ? files.Listing : null,
            Error = null,
            NotAFolder = false,
            Generation = generation,
            Way = way,
        };
        return (state with { Files = next }, [new ListFilesEffect(generation, path, way)]);
    }

    /// <summary>The connection is gone: a listing still on its way is dropped, and the tab reads again on the next session.</summary>
    private static FilesState FilesAfterConnectionLost(FilesState files) => files with
    {
        Generation = files.Generation + 1,
        Status = files.Status == FilesStatus.Loading ? FilesStatus.Idle : files.Status,
    };
}

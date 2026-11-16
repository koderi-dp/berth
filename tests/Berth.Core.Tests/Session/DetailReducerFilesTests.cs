using Berth.Engine.Models;
using Berth.Files;
using Berth.Session;

namespace Berth.Tests.Session;

/// <summary>The Files tab's listing: which way a folder is read, the fall-back, and which answers count.</summary>
public sealed class DetailReducerFilesTests
{
    private static readonly ContainerPath Etc = ContainerPath.Root.Join("etc");

    private static (DetailState State, DetailEffect[] Effects) Reduce(DetailState state, DetailEvent e)
    {
        var (next, effects) = DetailReducer.Reduce(state, e);
        return (next, effects.ToArray());
    }

    private static DetailState Loaded(bool running = true, bool paused = false)
    {
        var inspection = FakeEngineApi.Inspection("c", running);
        inspection = inspection with { Details = inspection.Details with { State = inspection.Details.State with { Paused = paused } } };
        var (opened, _) = Reduce(DetailState.Initial("c"), new DetailOpenRequested());
        return Reduce(opened, new InspectLoaded(opened.InspectGeneration, inspection)).State;
    }

    private static FileListing Listing(ContainerPath path, ListingWay way, params string[] names) =>
        new(path, [.. names.Select(n => new FileEntry(n, FileKind.File, 1, null, "-rw-r--r--", null))], way, false, false);

    [Fact]
    public void Browsing_a_running_container_reads_through_exec()
    {
        var (state, effects) = Reduce(Loaded(), new FilesBrowseRequested(Etc));

        Assert.Equal(FilesStatus.Loading, state.Files.Status);
        Assert.Equal(Etc, state.Files.Path);
        Assert.Equal([new ListFilesEffect(1, Etc, ListingWay.Exec)], effects);
        Assert.Equal(ListingWay.Exec, state.Files.Way);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Browsing_a_stopped_or_paused_container_reads_through_the_archive(bool running, bool paused)
    {
        var (_, effects) = Reduce(Loaded(running, paused), new FilesBrowseRequested(Etc));

        Assert.Equal([new ListFilesEffect(1, Etc, ListingWay.Archive)], effects);
    }

    [Fact]
    public void Nothing_is_read_before_the_container_was_read_or_after_the_connection_was_lost()
    {
        var opened = Reduce(DetailState.Initial("c"), new DetailOpenRequested()).State;
        Assert.Same(opened, Reduce(opened, new FilesBrowseRequested(Etc)).State);

        var lost = Reduce(Loaded(), new DetailConnectionLost()).State;
        Assert.Same(lost, Reduce(lost, new FilesBrowseRequested(Etc)).State);
    }

    [Fact]
    public void A_listing_is_shown_and_an_older_one_is_ignored()
    {
        var first = Reduce(Loaded(), new FilesBrowseRequested(Etc)).State;
        var second = Reduce(first, new FilesBrowseRequested(ContainerPath.Root)).State;

        var stale = Reduce(second, new FilesListed(1, Listing(Etc, ListingWay.Exec, "hosts")));
        Assert.Same(second, stale.State);

        var (shown, _) = Reduce(second, new FilesListed(2, Listing(ContainerPath.Root, ListingWay.Exec, "etc")));
        Assert.Equal(FilesStatus.Loaded, shown.Files.Status);
        Assert.Equal(["etc"], shown.Files.Listing!.Entries.Select(e => e.Name));
    }

    [Fact]
    public void A_failed_exec_listing_falls_back_to_the_archive_under_the_same_generation()
    {
        var loading = Reduce(Loaded(), new FilesBrowseRequested(Etc)).State;

        var (state, effects) = Reduce(loading, new ExecListingFailed(1, ExecListingFailure.Exited(ExecListing.CannotEnter)));

        Assert.Equal([new ListFilesEffect(1, Etc, ListingWay.Archive)], effects);
        Assert.Equal(ListingWay.Archive, state.Files.Way);
        Assert.False(state.Files.ExecUnavailable);
        Assert.Equal(FilesStatus.Loading, state.Files.Status);
    }

    [Fact]
    public void An_exec_the_engine_refuses_is_not_tried_again_in_that_container()
    {
        var loading = Reduce(Loaded(), new FilesBrowseRequested(Etc)).State;
        var fallen = Reduce(loading, new ExecListingFailed(1, ExecListingFailure.Refused(409))).State;
        var listed = Reduce(fallen, new FilesListed(1, Listing(Etc, ListingWay.Archive, "hosts"))).State;

        var (_, effects) = Reduce(listed, new FilesRefreshRequested());

        Assert.True(listed.Files.ExecUnavailable);
        Assert.Equal([new ListFilesEffect(2, Etc, ListingWay.Archive)], effects);
    }

    [Fact]
    public void An_exec_failure_after_the_fall_back_is_ignored()
    {
        var loading = Reduce(Loaded(), new FilesBrowseRequested(Etc)).State;
        var fallen = Reduce(loading, new ExecListingFailed(1, ExecListingFailure.Unreadable)).State;

        Assert.Same(fallen, Reduce(fallen, new ExecListingFailed(1, ExecListingFailure.Unreadable)).State);
    }

    [Fact]
    public void A_failed_archive_listing_shows_the_engines_message()
    {
        var loading = Reduce(Loaded(running: false), new FilesBrowseRequested(Etc)).State;

        var (state, _) = Reduce(loading, new FilesListFailed(1, "Could not find the file /etc in container c", NotAFolder: false));

        Assert.Equal(FilesStatus.Failed, state.Files.Status);
        Assert.Equal("Could not find the file /etc in container c", state.Files.Error);
        Assert.Null(state.Files.Listing);
    }

    [Fact]
    public void Not_a_folder_is_told_apart()
    {
        var loading = Reduce(Loaded(running: false), new FilesBrowseRequested(Etc.Join("hosts"))).State;

        var (state, _) = Reduce(loading, new FilesListFailed(1, "/etc/hosts is not a folder.", NotAFolder: true));

        Assert.True(state.Files.NotAFolder);
    }

    [Fact]
    public void Refresh_keeps_the_rows_shown_while_the_same_folder_is_read_again_but_another_folder_starts_empty()
    {
        var loading = Reduce(Loaded(), new FilesBrowseRequested(Etc)).State;
        var listed = Reduce(loading, new FilesListed(1, Listing(Etc, ListingWay.Exec, "hosts"))).State;

        var refreshing = Reduce(listed, new FilesRefreshRequested()).State;
        Assert.Equal(["hosts"], refreshing.Files.Listing!.Entries.Select(e => e.Name));
        Assert.Equal(FilesStatus.Loading, refreshing.Files.Status);

        var elsewhere = Reduce(listed, new FilesBrowseRequested(ContainerPath.Root)).State;
        Assert.Null(elsewhere.Files.Listing);
    }

    [Fact]
    public void The_first_refresh_reads_the_root()
    {
        var (_, effects) = Reduce(Loaded(), new FilesRefreshRequested());

        Assert.Equal([new ListFilesEffect(1, ContainerPath.Root, ListingWay.Exec)], effects);
    }

    [Fact]
    public void A_lost_connection_drops_a_listing_still_on_its_way()
    {
        var loading = Reduce(Loaded(), new FilesBrowseRequested(Etc)).State;
        var lost = Reduce(loading, new DetailConnectionLost()).State;

        Assert.Same(lost, Reduce(lost, new FilesListed(1, Listing(Etc, ListingWay.Exec, "hosts"))).State);
        Assert.Equal(FilesStatus.Idle, lost.Files.Status);
    }
}

using System.Collections.Immutable;
using Berth.Files;
using Berth.Presentation;
using Berth.Session;

namespace Berth.Tests.Presentation;

/// <summary>The Files tab: its rows, its notes for each state, and what a download or an upload asks first.</summary>
public sealed class FilesViewTests
{
    private static readonly ContainerPath Etc = ContainerPath.Root.Join("etc");

    private static readonly DateTimeOffset Noon = new(2026, 11, 16, 12, 30, 0, TimeSpan.Zero);

    private static FileEntry Entry(string name, FileKind kind, long? size = null, string? target = null) =>
        new(name, kind, size, Noon, kind == FileKind.Folder ? "drwxr-xr-x" : "-rw-r--r--", target);

    private static FileListing Listing(ListingWay way, params FileEntry[] entries) => new(Etc, [.. entries], way, false, false);

    private static FilesState Loaded(FileListing listing) =>
        FilesState.Initial with { Path = listing.Path, Status = FilesStatus.Loaded, Listing = listing, Way = listing.Way, Generation = 1 };

    [Fact]
    public void Rows_put_folders_first_then_sort_by_name_ignoring_case()
    {
        var listing = Listing(
            ListingWay.Exec,
            Entry("zeta", FileKind.File, 1),
            Entry("Beta", FileKind.Folder),
            Entry("alpha", FileKind.File, 2),
            Entry("apk", FileKind.Folder),
            Entry("Alpha", FileKind.File, 3),
            Entry("mtab", FileKind.Link, target: "/proc/mounts"));

        var rows = FileRows.Build(listing, TimeZoneInfo.Utc);

        Assert.Equal(["apk", "Beta", "Alpha", "alpha", "mtab", "zeta"], rows.Select(r => r.Name));
    }

    [Fact]
    public void A_row_carries_its_path_and_texts()
    {
        var rows = FileRows.Build(
            Listing(ListingWay.Exec, Entry("hosts", FileKind.File, 1536), Entry("apk", FileKind.Folder), Entry("mtab", FileKind.Link, target: "/proc/mounts")),
            TimeZoneInfo.Utc);
        var byName = rows.ToDictionary(r => r.Name);

        Assert.Equal("/etc/hosts", byName["hosts"].Path.Value);
        Assert.Equal("1.5 KB", byName["hosts"].SizeText);
        Assert.Equal("", byName["apk"].SizeText);
        Assert.Equal("2026-11-16 12:30", byName["hosts"].ModifiedText);
        Assert.Equal("mtab → /proc/mounts", byName["mtab"].Shown);
        Assert.Equal("hosts", byName["hosts"].Shown);
        Assert.True(byName["apk"].Opens);
        Assert.True(byName["mtab"].Opens);
        Assert.False(byName["hosts"].Opens);
    }

    [Fact]
    public void A_name_with_a_line_break_is_shown_on_one_line()
    {
        var row = Assert.Single(FileRows.Build(Listing(ListingWay.Exec, Entry("new\nline", FileKind.File, 1)), TimeZoneInfo.Utc));

        Assert.Equal("new\nline", row.Name);
        Assert.Equal("new⏎line", row.Shown);
    }

    [Fact]
    public void The_same_listing_builds_the_same_rows()
    {
        var listing = Listing(ListingWay.Exec, Entry("a", FileKind.File, 1));

        Assert.Equal(FileRows.Build(listing, TimeZoneInfo.Utc).ToArray(), FileRows.Build(listing, TimeZoneInfo.Utc).ToArray());
    }

    [Fact]
    public void An_empty_folder_says_so()
    {
        Assert.Equal("This folder is empty.", FilesView.Of(Loaded(Listing(ListingWay.Exec)), waiting: false).Empty);
    }

    [Fact]
    public void A_listing_read_through_the_archive_says_so()
    {
        var view = FilesView.Of(Loaded(Listing(ListingWay.Archive, Entry("a", FileKind.File, 1))), waiting: false);

        Assert.Equal("Listing through the archive: sizes of folders are not shown.", view.WayNote);
        Assert.Null(FilesView.Of(Loaded(Listing(ListingWay.Exec, Entry("a", FileKind.File, 1))), waiting: false).WayNote);
    }

    [Fact]
    public void An_incomplete_or_capped_listing_says_so()
    {
        var incomplete = Loaded(new FileListing(Etc, [Entry("a", FileKind.File, 1)], ListingWay.Archive, Incomplete: true, Capped: false));
        var capped = Loaded(new FileListing(Etc, [Entry("a", FileKind.File, 1)], ListingWay.Exec, Incomplete: false, Capped: true));
        var unread = Loaded(new FileListing(Etc, [Entry("a", FileKind.File, 1)], ListingWay.Exec, Incomplete: true, Capped: false));

        Assert.Equal("The listing stopped early: this folder is too large to read through the archive. Some entries are not shown.", FilesView.Of(incomplete, false).Note);
        Assert.Equal("Showing the first 10,000 entries.", FilesView.Of(capped, false).Note);
        Assert.Equal("Some entries could not be read.", FilesView.Of(unread, false).Note);
    }

    [Fact]
    public void A_failure_shows_the_engines_message_as_it_is()
    {
        var failed = FilesState.Initial with { Path = Etc, Status = FilesStatus.Failed, Error = "Could not find the file /etc in container c" };

        var view = FilesView.Of(failed, waiting: false);

        Assert.Equal("Could not find the file /etc in container c", view.Error);
        Assert.Null(view.Empty);
    }

    [Fact]
    public void Reading_a_folder_before_its_rows_are_known_says_which()
    {
        var loading = FilesState.Initial with { Path = Etc, Status = FilesStatus.Loading, Generation = 1 };

        Assert.Equal("Reading /etc…", FilesView.Of(loading, waiting: false).Loading);
        Assert.Null(FilesView.Of(loading with { Listing = Listing(ListingWay.Exec) }, waiting: false).Loading);
    }

    [Fact]
    public void While_waiting_for_the_engine_the_tab_says_so()
    {
        var view = FilesView.Of(Loaded(Listing(ListingWay.Exec)), waiting: true);

        Assert.Equal("Waiting for the engine…", view.Waiting);
    }

    [Fact]
    public void The_breadcrumb_is_the_path_from_the_root()
    {
        var crumbs = FilesView.Breadcrumb(Etc.Join("nginx"));

        Assert.Equal(["/", "etc", "nginx"], crumbs.Select(c => c.Name));
        Assert.Equal("/etc", crumbs[1].Path.Value);
    }

    [Fact]
    public void A_download_over_nothing_asks_nothing()
    {
        Assert.Null(TransferConfirmations.Download(["hosts", "apk"], @"C:\d", _ => false));
    }

    [Fact]
    public void A_download_over_what_is_there_asks_to_replace_it_naming_it()
    {
        var there = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\d\hosts", @"C:\d\_CON" };

        var confirm = TransferConfirmations.Download(["hosts", "apk", "CON"], @"C:\d", there.Contains);

        Assert.NotNull(confirm);
        Assert.Equal("Replace 2 items?", confirm.Title);
        Assert.Equal(["hosts", "_CON"], confirm.Names);
        Assert.Equal("Replace", confirm.Primary);
        Assert.Contains("already in C:\\d", confirm.Text, StringComparison.Ordinal);
        Assert.Contains("A folder that is already there keeps its other files.", confirm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_upload_over_a_same_named_entry_asks_first_and_says_it_cannot_be_undone()
    {
        // The names come from the engine just before sending (the transfer asks), not from the listing on screen.
        var confirm = TransferConfirmations.Upload(["notes.txt"], Etc);

        Assert.Equal("Replace 1 item in /etc?", confirm.Title);
        Assert.Equal(["notes.txt"], confirm.Names);
        Assert.Contains("An upload cannot be undone.", confirm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_names_are_listed_up_to_five()
    {
        var names = Enumerable.Range(1, 8).Select(i => $"f{i}").ToList();

        var confirm = TransferConfirmations.Download(names, @"C:\d", _ => true)!;

        Assert.Contains("f1, f2, f3, f4, f5 and 3 more", confirm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Transfers_shown_are_this_containers_newest_first()
    {
        var a1 = new FileTransfer(1, new DownloadRequest("a", [Etc], @"C:\d", false), TransferStatus.Done, 0, null, null, null);
        var b = new FileTransfer(2, new DownloadRequest("b", [Etc], @"C:\d", false), TransferStatus.Running, 0, null, null, null);
        var a3 = new FileTransfer(3, new UploadRequest("a", [@"C:\x"], Etc), TransferStatus.Running, 0, null, null, null);
        var all = ImmutableDictionary<int, FileTransfer>.Empty.Add(1, a1).Add(2, b).Add(3, a3);

        Assert.Equal([3, 1], TransferView.Of(all, "a").Select(t => t.Id));
    }

    [Fact]
    public void Rows_sort_by_size_or_modified_keeping_folders_first()
    {
        var listing = new FileListing(
            Etc,
            [
                Entry("big", FileKind.File, 300) with { Modified = Noon.AddDays(-2) },
                Entry("small", FileKind.File, 1) with { Modified = Noon },
                Entry("zdir", FileKind.Folder) with { Modified = Noon.AddDays(-5) },
                Entry("adir", FileKind.Folder) with { Modified = Noon },
            ],
            ListingWay.Exec,
            false,
            false);

        Assert.Equal(["adir", "zdir", "small", "big"], FileRows.Build(listing, TimeZoneInfo.Utc, new ListSort<FileColumn>(FileColumn.Size, false)).Select(r => r.Name));
        Assert.Equal(["adir", "zdir", "big", "small"], FileRows.Build(listing, TimeZoneInfo.Utc, new ListSort<FileColumn>(FileColumn.Size, true)).Select(r => r.Name));
        Assert.Equal(["zdir", "adir", "big", "small"], FileRows.Build(listing, TimeZoneInfo.Utc, new ListSort<FileColumn>(FileColumn.Modified, false)).Select(r => r.Name));
        Assert.Equal(["zdir", "adir", "small", "big"], FileRows.Build(listing, TimeZoneInfo.Utc, new ListSort<FileColumn>(FileColumn.Name, true)).Select(r => r.Name));
    }
}
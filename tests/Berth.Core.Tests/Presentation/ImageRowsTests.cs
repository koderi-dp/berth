using Berth.Presentation;
using Berth.State;
using static Berth.Tests.Presentation.ResourceFixtures;

namespace Berth.Tests.Presentation;

public sealed class ImageRowsTests
{
    private static readonly ListSort<ImageColumn> ByName = ImageRows.DefaultSort;

    [Fact]
    public void An_image_has_one_row_per_tag()
    {
        var state = StateWith(images: [Image("sha256:0123456789abcdef", ["nginx:latest", "registry:5000/team/nginx:1.27"], size: 4096, created: 1_700_000_100)]);

        var rows = ImageRows.From(state);

        Assert.Equal(["nginx:latest", "registry:5000/team/nginx:1.27"], rows.Select(r => r.Key));
        Assert.Equal(["nginx", "registry:5000/team/nginx"], rows.Select(r => r.Name));
        Assert.Equal(["latest", "1.27"], rows.Select(r => r.Tag));
        Assert.All(rows, r =>
        {
            Assert.Equal("sha256:0123456789abcdef", r.ImageId);
            Assert.Equal("0123456789ab", r.ShortId);
            Assert.Equal(4096, r.Size);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_100), r.Created);
            Assert.Equal(2, r.TagCount);
            Assert.True(r.IsTagged);
        });
    }

    [Fact]
    public void An_untagged_image_is_one_row_named_none_keyed_by_its_id()
    {
        var state = StateWith(images: [Image("sha256:aaaabbbbccccdddd")]);

        var row = Assert.Single(ImageRows.From(state));

        Assert.Equal("sha256:aaaabbbbccccdddd", row.Key);
        Assert.Equal("<none>", row.Name);
        Assert.Equal("<none>", row.Tag);
        Assert.False(row.IsTagged);
        Assert.True(row.IsDangling);
        Assert.Equal("<none>:<none> (aaaabbbbcccc)", row.DisplayName);
    }

    [Fact]
    public void An_image_with_only_a_digest_is_named_after_the_digest_repository()
    {
        var state = StateWith(images: [Image("sha256:aaaabbbbccccdddd", digests: ["ghcr.io/org/app@sha256:feed"])]);

        var row = Assert.Single(ImageRows.From(state));

        Assert.Equal("ghcr.io/org/app", row.Name);
        Assert.Equal("<none>", row.Tag);
        Assert.Equal("sha256:aaaabbbbccccdddd", row.Key);
    }

    [Fact]
    public void An_image_is_in_use_by_containers_of_any_state_and_names_them_from_the_container_list()
    {
        var state = StateWith(
            images: [Image("sha256:a", ["a:1"], containers: 2), Image("sha256:b", ["b:1"], containers: 0)],
            containers: [User("web", imageId: "sha256:a", state: "running"), User("job", imageId: "sha256:a", state: "exited")]);

        var rows = ImageRows.From(state);

        Assert.True(rows[0].InUse);
        Assert.Equal(2, rows[0].UsedByCount);
        Assert.Equal(["job", "web"], rows[0].UsedBy);
        Assert.False(rows[1].InUse);
        Assert.Empty(rows[1].UsedBy);
    }

    [Fact]
    public void An_image_the_engine_did_not_count_is_counted_from_the_container_list()
    {
        var state = StateWith(images: [Image("sha256:a", ["a:1"], containers: null)], containers: [User("web", imageId: "sha256:a")]);

        var row = Assert.Single(ImageRows.From(state));

        Assert.Equal(1, row.UsedByCount);
        Assert.True(row.InUse);
    }

    [Fact]
    public void Every_row_of_an_image_is_pending_while_any_of_its_tags_or_the_image_is_being_removed()
    {
        var images = new[] { Image("sha256:a", ["a:1", "a:2"]), Image("sha256:b", ["b:1"]) };
        var state = Pending(Pending(StateWith(images: images), ResourceKind.Images, "a:1"), ResourceKind.Images, "sha256:b");

        var rows = ImageRows.From(state).ToDictionary(r => r.Key);

        Assert.Equal(ItemAction.Remove, rows["a:1"].Pending);
        Assert.Equal(ItemAction.Remove, rows["a:2"].Pending);
        Assert.Equal(ItemAction.Remove, rows["b:1"].Pending);
    }

    [Theory]
    [InlineData(ImageShow.All, new[] { "a:1", "a:2", "b:1", "sha256:c" })]
    [InlineData(ImageShow.InUse, new[] { "a:1", "a:2" })]
    [InlineData(ImageShow.Unused, new[] { "b:1", "sha256:c" })]
    [InlineData(ImageShow.Dangling, new[] { "sha256:c" })]
    public void The_filter_shows_all_in_use_unused_or_dangling(ImageShow show, string[] keys)
    {
        var state = StateWith(
            images: [Image("sha256:a", ["a:1", "a:2"], containers: 1), Image("sha256:b", ["b:1"]), Image("sha256:c")],
            containers: [User("web", imageId: "sha256:a")]);

        var rows = ImageRows.Shown(ImageRows.From(state), new ImageFilter("", show), ByName);

        Assert.Equal(keys.Order(), rows.Select(r => r.Key).Order());
    }

    [Theory]
    [InlineData("NGINX", new[] { "nginx:latest" })]
    [InlineData("alpine", new[] { "alpine:3.19" })]
    [InlineData("3.19", new[] { "alpine:3.19" })]
    [InlineData("cafe", new[] { "nginx:latest" })]
    [InlineData("  ", new[] { "alpine:3.19", "nginx:latest" })]
    [InlineData("sha", new string[0])]
    [InlineData("256", new string[0])]
    public void The_search_matches_name_tag_or_id_ignoring_case(string search, string[] keys)
    {
        var state = StateWith(images: [Image("sha256:cafe0001", ["nginx:latest"]), Image("sha256:beef0002", ["alpine:3.19"])]);

        var rows = ImageRows.Shown(ImageRows.From(state), new ImageFilter(search, ImageShow.All), ByName);

        Assert.Equal(keys, rows.Select(r => r.Key));
    }

    [Theory]
    [InlineData(ImageColumn.Name, false, new[] { "alpine:3.19", "Nginx:1", "nginx:latest" })]
    [InlineData(ImageColumn.Name, true, new[] { "nginx:latest", "Nginx:1", "alpine:3.19" })]
    [InlineData(ImageColumn.Tag, false, new[] { "Nginx:1", "alpine:3.19", "nginx:latest" })]
    [InlineData(ImageColumn.Created, false, new[] { "nginx:latest", "alpine:3.19", "Nginx:1" })]
    [InlineData(ImageColumn.Size, true, new[] { "Nginx:1", "alpine:3.19", "nginx:latest" })]
    public void Rows_sort_by_name_tag_created_or_size(ImageColumn column, bool descending, string[] keys)
    {
        var state = StateWith(images:
        [
            Image("sha256:a", ["nginx:latest"], created: 100, size: 10),
            Image("sha256:b", ["alpine:3.19"], created: 200, size: 20),
            Image("sha256:c", ["Nginx:1"], created: 300, size: 30),
        ]);

        var rows = ImageRows.Shown(ImageRows.From(state), ImageFilter.All, new ListSort<ImageColumn>(column, descending));

        Assert.Equal(keys, rows.Select(r => r.Key));
    }

    [Fact]
    public void The_empty_list_says_why()
    {
        Assert.Equal("No images on this engine.", ImageRows.EmptyText(engineHasImages: false, ImageFilter.All));
        Assert.Equal("No images match the search.", ImageRows.EmptyText(engineHasImages: true, new ImageFilter("x", ImageShow.All)));
        Assert.Equal("No images are in use.", ImageRows.EmptyText(engineHasImages: true, new ImageFilter("", ImageShow.InUse)));
        Assert.Equal("No images are unused.", ImageRows.EmptyText(engineHasImages: true, new ImageFilter("", ImageShow.Unused)));
        Assert.Equal("No dangling images.", ImageRows.EmptyText(engineHasImages: true, new ImageFilter("", ImageShow.Dangling)));
    }
}

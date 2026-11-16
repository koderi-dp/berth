using Berth.Engine.Models;
using Berth.Presentation;
using static Berth.Tests.Presentation.ResourceFixtures;

namespace Berth.Tests.Presentation;

/// <summary>The line above the images table, and the run dialog's note about host paths.</summary>
public sealed class ImageUsageTests
{
    private static DiskUsage Usage(long total, long reclaimable) =>
        new([], new UsageTotals(3, 1, total, reclaimable), [], new UsageTotals(0, 0, 0, 0));

    [Fact]
    public void The_usage_line_says_what_is_in_use_of_the_total_and_how_many_images()
    {
        var state = StateWith(images: [Image("sha256:a", ["a:1", "a:2"]), Image("sha256:b"), Image("sha256:c", ["c:1"])], usage: Usage(1_288_490_189, 1_218_235_597));

        Assert.Equal("67 MB in use of 1.2 GB · 3 images", ImageRows.UsageLine(state));
    }

    [Fact]
    public void Until_the_disk_use_arrives_the_line_says_it_is_being_calculated()
    {
        var state = StateWith(images: [Image("sha256:a", ["a:1"])]);

        Assert.Equal("Calculating… · 1 image", ImageRows.UsageLine(state));
    }

    [Theory]
    [InlineData("sha256:aaaaaaaaaaaaaaaa", "web:1, web:latest")]
    [InlineData("sha256:bbbbbbbbbbbbbbbb", "<none>:<none> (bbbbbbbbbbbb)")]
    [InlineData("web:1", "web:1")]
    [InlineData("sha256:gone", "sha256:gone")]
    public void A_removal_key_is_named_by_the_image_tags_or_short_id_while_it_is_listed(string key, string name)
    {
        var state = StateWith(images: [Image("sha256:aaaaaaaaaaaaaaaa", ["web:1", "web:latest"]), Image("sha256:bbbbbbbbbbbbbbbb")]);

        Assert.Equal(name, ImageRows.NameOfKey(state, key));
    }

    [Theory]
    [InlineData("Ubuntu 24.04.5 LTS", "6.18.58.1-microsoft-standard-WSL2", true)]
    [InlineData("Docker Desktop", "6.6.87.2-microsoft-standard-WSL2", false)]
    [InlineData("Docker Desktop", "7.0.14-linuxkit", false)]
    [InlineData("Ubuntu 24.04 LTS", "6.8.0-45-generic", false)]
    [InlineData("", "", false)]
    public void The_host_path_note_shows_only_for_an_engine_running_in_WSL_itself(string os, string kernel, bool shown)
    {
        var info = new EngineInfo("host", "29.0.0", 8, 1L << 30, 0, 0, 0, 0) { OperatingSystem = os, KernelVersion = kernel };

        Assert.Equal(shown ? RunDraft.HostPathNote : null, RunDraft.HostPathNoteFor(info));
    }

    [Fact]
    public void No_host_path_note_before_the_engine_has_been_read()
    {
        Assert.Null(RunDraft.HostPathNoteFor(null));
    }
}

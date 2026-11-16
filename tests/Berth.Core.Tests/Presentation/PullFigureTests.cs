using Berth.Engine.Models;
using Berth.Presentation;
using Berth.State;

namespace Berth.Tests.Presentation;

public sealed class PullFigureTests
{
    private const long MB = 1 << 20;

    private static ImagePull Pull(params PullProgress[] lines) => ImagePull.Start("nginx:latest", 1).With(lines);

    [Fact]
    public void Layers_and_bytes_add_up_to_one_figure()
    {
        var pull = Pull(
            new PullProgress("l1", "Pull complete", null, null),
            new PullProgress("l2", "Already exists", null, null),
            new PullProgress("l3", "Downloading", 20 * MB, 60 * MB),
            new PullProgress("l4", "Downloading", 1 * MB, 40 * MB),
            new PullProgress("l5", "Waiting", null, null),
            new PullProgress("l6", "Pulling fs layer", null, null),
            new PullProgress("l7", "Extracting", 3 * MB, 20 * MB));

        var figure = PullFigure.Of(pull);

        Assert.Equal(2, figure.LayersDone);
        Assert.Equal(7, figure.LayersTotal);
        Assert.Equal(41 * MB, figure.BytesDone);
        Assert.Equal(120 * MB, figure.BytesTotal);
        Assert.Equal("Downloading 2 of 7 layers, 41 MB of 120 MB", figure.Text);
    }

    [Fact]
    public void A_layer_past_its_download_counts_all_its_bytes()
    {
        var pull = Pull(
            new PullProgress("l1", "Downloading", 5, 10),
            new PullProgress("l1", "Download complete", null, null),
            new PullProgress("l2", "Downloading", 10, 10),
            new PullProgress("l2", "Verifying Checksum", null, null));

        var figure = PullFigure.Of(pull);

        Assert.Equal(20, figure.BytesDone);
        Assert.Equal(20, figure.BytesTotal);
        Assert.Equal("Extracting 0 of 2 layers, 20 B of 20 B", figure.Text);
    }

    [Fact]
    public void Layers_that_report_no_size_leave_the_bytes_unknown()
    {
        var figure = PullFigure.Of(Pull(new PullProgress("l1", "Waiting", null, null), new PullProgress("l2", "Pull complete", null, null)));

        Assert.Null(figure.BytesDone);
        Assert.Null(figure.BytesTotal);
        Assert.Equal(0.5, figure.Fraction);
        Assert.Equal("Downloading 1 of 2 layers", figure.Text);
    }

    [Fact]
    public void The_figure_never_goes_above_one_hundred_percent()
    {
        var figure = PullFigure.Of(Pull(new PullProgress("l1", "Downloading", 150, 100), new PullProgress("l2", "Pull complete", null, 50)));

        Assert.Equal(150, figure.BytesDone);
        Assert.Equal(150, figure.BytesTotal);
        Assert.InRange(figure.Fraction!.Value, 0, 1);
    }

    [Fact]
    public void The_fraction_weighs_bytes_downloaded_and_layers_done_equally()
    {
        var figure = PullFigure.Of(Pull(new PullProgress("l1", "Downloading", 50, 100), new PullProgress("l2", "Pull complete", null, 100)));

        Assert.Equal(0.5 * (150.0 / 200) + 0.5 * 0.5, figure.Fraction!.Value, precision: 6);
    }

    [Fact]
    public void A_pull_with_no_layer_yet_says_it_is_starting()
    {
        var figure = PullFigure.Of(Pull(new PullProgress("latest", "Pulling from library/nginx", null, null)));

        Assert.Equal(0, figure.LayersTotal);
        Assert.Null(figure.Fraction);
        Assert.Equal("Pulling from library/nginx", figure.Text);
        Assert.Equal("Starting…", PullFigure.Of(Pull()).Text);
    }

    [Fact]
    public void A_finished_pull_is_complete_and_a_failed_one_shows_the_engine_message()
    {
        var done = PullFigure.Of(Pull(new PullProgress("l1", "Pull complete", null, 10)) with { Status = PullStatus.Done });
        var failed = PullFigure.Of(Pull(new PullProgress("l1", "Downloading", 5, 10)) with { Status = PullStatus.Failed, Error = "manifest unknown" });

        Assert.Equal(1.0, done.Fraction);
        Assert.Equal("Pull complete", done.Text);
        Assert.Equal("manifest unknown", failed.Text);
    }

    [Fact]
    public void An_interrupted_pull_says_the_connection_changed()
    {
        var figure = PullFigure.Of(Pull(new PullProgress("l1", "Downloading", 5, 10)) with { Status = PullStatus.Interrupted });

        Assert.Equal("Stopped: the connection to the engine changed.", figure.Text);
    }
}

using Berth.Presentation;
using Berth.State;

namespace Berth.Tests.Presentation;

public sealed class RemovalProgressTests
{
    private static readonly RemovalBatch Ten = new(10, 0, 0, [], []);

    [Fact]
    public void A_running_batch_says_how_far_it_is()
    {
        Assert.Equal("Removing: 3 of 10 done.", RemovalProgress.Summary(Ten with { Finished = 3, Running = 4 }));
    }

    [Fact]
    public void An_ended_batch_says_what_was_removed_refused_and_skipped()
    {
        var batch = Ten with { Finished = 10, Refusals = [new RemovalRefusal("v2", "volume is in use")] } with { Skips = [new RemovalSkip("v3", SkipReason.Gone)] };

        Assert.Equal("Removed 8 of 10. 1 could not be removed. 1 was skipped.", RemovalProgress.Summary(batch));
        Assert.Equal(["v2: volume is in use", "v3: skipped, it is no longer listed."], RemovalProgress.Details(batch));
        Assert.Equal("Removed 10 of 10.", RemovalProgress.Summary(Ten with { Finished = 10 }));
    }

    [Fact]
    public void Each_skip_says_why()
    {
        var batch = Ten with { Finished = 10 } with
        {
            Skips = [new RemovalSkip("sha256:1", SkipReason.TagMoved), new RemovalSkip("v4", SkipReason.InUse)],
        };

        Assert.Equal(
            ["sha256:1: skipped, a tag it was to lose now names another image.", "v4: skipped, a container uses it now."],
            RemovalProgress.Details(batch));
    }

    [Fact]
    public void An_interrupted_batch_says_the_connection_changed_and_what_was_not_done()
    {
        var batch = Ten with { Finished = 3 } with { Interrupted = true, NotSent = 5, Unanswered = 2 };

        Assert.Equal(
            "The connection to the engine changed. Removed 3 of 10; 5 were not sent, and whether 2 were removed is not known.",
            RemovalProgress.Summary(batch));
    }
}

using Berth.State;

namespace Berth.Presentation;

/// <summary>What a list says about its batch of removes (<see cref="RemovalBatch"/>): one summary line and a line per refusal or skip.</summary>
public static class RemovalProgress
{
    /// <summary>
    /// "Removing: 3 of 10 done." while it runs; "Removed 8 of 10. 1 could not be removed. 1 was skipped." once ended; and when the
    /// connection changed, that it did, how many were removed, not sent, and sent without an answer.
    /// </summary>
    public static string Summary(RemovalBatch batch)
    {
        if (batch.IsRunning)
            return $"Removing: {batch.Finished} of {batch.Total} done.";

        if (batch.Interrupted)
        {
            var parts = new List<string> { $"Removed {batch.Removed} of {batch.Total}" };
            if (batch.NotSent > 0)
                parts.Add($"{batch.NotSent} {(batch.NotSent == 1 ? "was" : "were")} not sent");
            if (batch.Unanswered > 0)
                parts.Add($"whether {batch.Unanswered} {(batch.Unanswered == 1 ? "was" : "were")} removed is not known");
            var tail = parts.Count switch
            {
                1 => parts[0],
                2 => $"{parts[0]}; {parts[1]}",
                _ => $"{parts[0]}; {parts[1]}, and {parts[2]}",
            };
            return $"The connection to the engine changed. {tail}.{Problems(batch)}";
        }

        return $"Removed {batch.Removed} of {batch.Total}.{Problems(batch)}";
    }

    /// <summary>One line per refusal ("v2: the engine's message") and per skip ("v3: skipped, it is no longer listed."), in order.</summary>
    public static IReadOnlyList<string> Details(RemovalBatch batch) => Details(batch, key => key);

    /// <summary>As <see cref="Details(RemovalBatch)"/>, each item named by <paramref name="nameOf"/> its key (an image ID as its tags).</summary>
    public static IReadOnlyList<string> Details(RemovalBatch batch, Func<string, string> nameOf) =>
    [
        .. batch.Refusals.Select(r => $"{nameOf(r.Key)}: {r.Message}"),
        .. batch.Skips.Select(s => $"{nameOf(s.Id)}: skipped, {Why(s.Reason)}"),
    ];

    private static string Problems(RemovalBatch batch)
    {
        var text = "";
        if (batch.Refusals.Length > 0)
            text += $" {batch.Refusals.Length} could not be removed.";
        if (batch.Skips.Length > 0)
            text += $" {batch.Skips.Length} {(batch.Skips.Length == 1 ? "was" : "were")} skipped.";
        return text;
    }

    private static string Why(SkipReason reason) => reason switch
    {
        SkipReason.Gone => "it is no longer listed.",
        SkipReason.TagMoved => "a tag it was to lose now names another image.",
        _ => "a container uses it now.",
    };
}

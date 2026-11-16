using Berth.Engine.Models;

namespace Berth.State;

/// <summary>
/// The image the run dialog or the Images page's Inspect asked to read, until dismissed: being read, read, or failed. One at a time: a
/// request for another image replaces it, and the answer for the earlier one is dropped.
/// </summary>
/// <param name="Id">The image asked for, by ID or name.</param>
/// <param name="Generation">This read; answers of an earlier read are dropped.</param>
/// <param name="Inspection">The engine's answer once it arrived.</param>
/// <param name="Error">The engine's message when the read failed.</param>
public sealed record ImageInspect(string Id, int Generation, ImageInspection? Inspection, string? Error)
{
    /// <summary>The read is under way: neither an answer nor an error yet.</summary>
    public bool IsLoading => Inspection is null && Error is null;
}

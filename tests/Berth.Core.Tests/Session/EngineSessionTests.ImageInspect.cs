using Berth.Session;

namespace Berth.Tests.Session;

/// <summary>The runtime of reading one image for the run dialog and the Images page's Inspect.</summary>
public sealed partial class EngineSessionTests
{
    [Fact]
    public async Task An_image_inspect_reaches_the_state_with_the_engines_whole_answer()
    {
        var api = new FakeEngineApi();
        api.SetImage("alpine:3.19", FakeEngineApi.ImageDetails("sha256:a", "80/tcp"), """{"Id":"sha256:a","Config":{}}""");
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        h.Session.Dispatch(new ImageInspectRequested("alpine:3.19"));
        var state = await h.WaitFor(s => s.ImageInspect is { IsLoading: false });

        Assert.Equal("alpine:3.19", state.ImageInspect!.Id);
        Assert.Equal(["80/tcp"], state.ImageInspect.Inspection!.Details.ExposedPorts);
        Assert.Equal("""{"Id":"sha256:a","Config":{}}""", state.ImageInspect.Inspection.RawJson);
        Assert.Contains("InspectImage alpine:3.19", api.Calls);
    }

    [Fact]
    public async Task A_refused_image_inspect_carries_the_engines_message_and_is_reported()
    {
        var api = new FakeEngineApi();
        await using var h = Harness.With(api);
        await h.ConnectAsync();

        h.Session.Dispatch(new ImageInspectRequested("berth-check-nope"));
        var state = await h.WaitFor(s => s.ImageInspect is { IsLoading: false });

        Assert.Equal("No such image: berth-check-nope", state.ImageInspect!.Error);
        Assert.Equal(ConnectionStatus.Connected, state.Status);
        Assert.Equal("reading an image failed", (await h.WaitForError()).What);
    }

    [Fact]
    public async Task An_image_inspect_answered_after_the_connection_started_over_is_dropped()
    {
        var first = new FakeEngineApi { IgnoreCancellation = true };
        first.SetImage("sha256:a", FakeEngineApi.ImageDetails("sha256:a"));
        var gate = first.Hold("InspectImage");
        await using var h = Harness.With(first);
        try
        {
            await h.ConnectAsync();
            h.Session.Dispatch(new ImageInspectRequested("sha256:a"));
            await gate.Entered.WaitAsync(Patience);

            h.Session.Dispatch(new ConnectRequested());
            await h.WaitFor(s => s.Status == ConnectionStatus.Connected && s.Containers.Length > 0 && h.Created.Count == 2);
            var dropped = h.WaitForDropped<ImageInspected>();
            gate.Release();
            await dropped;

            Assert.Null(h.Session.State.ImageInspect);
        }
        finally
        {
            // The old connection is retired only once its calls end, and this one ignores cancelling.
            gate.Release();
            first.EndStream();
        }
    }
}

namespace Berth.Session;

/// <summary>A session that runs on one engine connection and is cut off when that connection is retired (a detail page's, the Logs page's).</summary>
internal interface IConnectionBound
{
    /// <summary>The connection is being retired: stop every call and wait for them, so the engine client can be disposed after.</summary>
    Task CutOffAsync();

    /// <summary><paramref name="release"/> is called when the session is disposed first, so the connection stops tracking it.</summary>
    void ReleaseOnDispose(Action release);
}

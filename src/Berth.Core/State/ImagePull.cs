using System.Collections.Immutable;
using Berth.Engine.Models;

namespace Berth.State;

/// <summary>Where a pull is: running, finished (until the image list shows the image), or failed (until dismissed or started again).</summary>
public enum PullStatus { Running, Done, Failed }

/// <summary>
/// One layer of a pull as its latest progress line left it. <see cref="Total"/> is the layer's size from the last line that carried
/// one, kept while later lines ("Download complete", "Pull complete") carry none; <see cref="Current"/> is from the latest line only.
/// </summary>
public sealed record PullLayer(string Id, string Status, long? Current, long? Total);

/// <summary>
/// An image pull the user started.
/// </summary>
/// <param name="Reference">The reference as pulled: name and tag ("nginx:latest") or name and digest ("nginx@sha256:...").</param>
/// <param name="Layers">Every layer in the order the engine first named it.</param>
/// <param name="LastStatus">The latest line about the whole pull ("Pulling from library/nginx", "Digest: ...", "Status: ...").</param>
/// <param name="Error">The engine's message when <see cref="Status"/> is <see cref="PullStatus.Failed"/>.</param>
/// <param name="Generation">This attempt; results of an earlier attempt at the same reference are dropped.</param>
/// <param name="FinishedAtLoad">
/// The image list's load generation when the pull finished: a load after it shows the image, so the entry goes then.
/// </param>
public sealed record ImagePull(
    string Reference,
    PullStatus Status,
    ImmutableArray<PullLayer> Layers,
    string? LastStatus,
    string? Error,
    int Generation,
    int FinishedAtLoad = 0)
{
    private const string PullingFrom = "Pulling from ";

    /// <summary>A pull of <paramref name="reference"/> that has just started.</summary>
    public static ImagePull Start(string reference, int generation) => new(reference, PullStatus.Running, [], null, null, generation);

    /// <summary>
    /// This pull with <paramref name="lines"/> applied in order. A line naming a layer updates that layer (or adds it at the end); a
    /// line naming none, or the "Pulling from ..." line (which names the tag), is the pull's <see cref="LastStatus"/>.
    /// </summary>
    public ImagePull With(IEnumerable<PullProgress> lines)
    {
        var layers = Layers.ToBuilder();
        var lastStatus = LastStatus;
        foreach (var line in lines)
        {
            if (line.LayerId.Length == 0 || line.Status.StartsWith(PullingFrom, StringComparison.Ordinal))
            {
                lastStatus = line.Status;
                continue;
            }

            var index = IndexOf(layers, line.LayerId);
            if (index < 0)
            {
                layers.Add(new PullLayer(line.LayerId, line.Status, line.Current, line.Total));
            }
            else
            {
                var layer = layers[index];
                layers[index] = layer with { Status = line.Status, Current = line.Current, Total = line.Total ?? layer.Total };
            }
        }

        return this with { Layers = layers.ToImmutable(), LastStatus = lastStatus };
    }

    /// <summary>
    /// The key a pull of <paramref name="typed"/> runs under: its name and tag ("nginx" is "nginx:latest") or its name and digest, split as
    /// <see cref="Engine.ImageReference.Parse"/> splits it; null when it is no reference (blank, or a missing name, tag or digest).
    /// </summary>
    public static string? KeyOf(string typed)
    {
        try
        {
            var reference = Engine.ImageReference.Parse(typed);
            // A tag cannot hold ":", a digest always does ("sha256:...").
            return reference.Tag.Contains(':') ? $"{reference.Name}@{reference.Tag}" : $"{reference.Name}:{reference.Tag}";
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static int IndexOf(ImmutableArray<PullLayer>.Builder layers, string id)
    {
        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i].Id == id)
                return i;
        }

        return -1;
    }
}

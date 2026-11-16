using System.Collections.Immutable;
using Berth.State;

namespace Berth.Presentation;

/// <summary>
/// A pull's progress as one figure: layers done of all layers named so far, bytes downloaded of the sizes known so far, the share done
/// (for a progress bar, never above 1; null before any layer is named) and the line under it.
/// </summary>
/// <param name="BytesDone">Bytes downloaded of the layers whose size is known; null while no layer has reported one.</param>
/// <param name="Fraction">
/// Half the share of bytes downloaded plus half the share of layers done (layers alone while no size is known), so the bar keeps moving
/// while layers are extracted after their download.
/// </param>
public sealed record PullFigure(int LayersDone, int LayersTotal, long? BytesDone, long? BytesTotal, double? Fraction, string Text)
{
    /// <summary>A layer that is on disk.</summary>
    private static readonly ImmutableHashSet<string> Done = ImmutableHashSet.Create(StringComparer.Ordinal, "Pull complete", "Already exists");

    /// <summary>A layer whose download is over.</summary>
    private static readonly ImmutableHashSet<string> Downloaded =
        ImmutableHashSet.Create(StringComparer.Ordinal, "Verifying Checksum", "Download complete", "Extracting", "Pull complete");

    /// <summary>
    /// The figure of <paramref name="pull"/>: "Downloading 3 of 7 layers, 41 MB of 120 MB" while any layer still downloads,
    /// "Extracting ..." once all are downloaded; the engine's last line about the whole pull, or "Starting…", before any layer is named;
    /// "Pull complete" when done; the engine's message when failed.
    /// </summary>
    public static PullFigure Of(ImagePull pull)
    {
        var layers = pull.Layers;
        var done = layers.Count(l => Done.Contains(l.Status));
        var sized = layers.Where(l => l.Total is > 0 && l.Status != "Already exists").ToList();
        long? bytesTotal = sized.Count == 0 ? null : sized.Sum(l => l.Total!.Value);
        long? bytesDone = sized.Count == 0 ? null : sized.Sum(DownloadedBytes);

        double? fraction = layers.Length == 0 ? null : (double)done / layers.Length;
        if (fraction is { } layerShare && bytesTotal is { } total)
            fraction = 0.5 * Math.Min(1, (double)bytesDone!.Value / total) + 0.5 * layerShare;

        switch (pull.Status)
        {
            case PullStatus.Done:
                return new PullFigure(layers.Length, layers.Length, bytesTotal, bytesTotal, 1, "Pull complete");
            case PullStatus.Failed:
                return new PullFigure(done, layers.Length, bytesDone, bytesTotal, fraction, pull.Error ?? "");
        }

        if (layers.Length == 0)
            return new PullFigure(0, 0, null, null, null, pull.LastStatus ?? "Starting…");

        var verb = layers.Any(l => !Downloaded.Contains(l.Status) && !Done.Contains(l.Status)) ? "Downloading" : "Extracting";
        var text = $"{verb} {done} of {layers.Length} layers";
        if (bytesTotal is { } known)
            text += $", {ByteFormat.Bytes(bytesDone!.Value)} of {ByteFormat.Bytes(known)}";
        return new PullFigure(done, layers.Length, bytesDone, bytesTotal, fraction, text);
    }

    /// <summary>A sized layer's bytes downloaded: what it reports while downloading (at most its size), all of it once past the download.</summary>
    private static long DownloadedBytes(PullLayer layer) =>
        Downloaded.Contains(layer.Status) ? layer.Total!.Value
        : layer.Status == "Downloading" ? Math.Clamp(layer.Current ?? 0, 0, layer.Total!.Value)
        : 0;
}

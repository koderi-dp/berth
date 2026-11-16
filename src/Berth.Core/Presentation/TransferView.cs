using System.Collections.Immutable;
using System.Globalization;
using Berth.Files;
using Berth.Session;

namespace Berth.Presentation;

/// <summary>
/// One transfer as the Files tab's panel shows it: what goes where, the progress or how it ended, what was left out and why
/// (<see cref="Notes"/>), the share done for a bar (null when the total is not known), and what can be done with it.
/// </summary>
/// <param name="ShowInFolder">The path a finished download offers to show in Explorer; null otherwise.</param>
public sealed record TransferLine(
    int Id, string Title, string Text, string? Notes, double? Fraction, bool IsRunning, bool IsError, bool CanCancel, bool CanDismiss, string? ShowInFolder);

/// <summary>The Files tab's transfer rows.</summary>
public static class TransferView
{
    /// <summary>The transfers of <paramref name="containerId"/>, newest first.</summary>
    public static ImmutableArray<TransferLine> Of(ImmutableDictionary<int, FileTransfer> transfers, string containerId) =>
        [.. transfers.Values.Where(t => t.Request.ContainerId == containerId).OrderByDescending(t => t.Id).Select(Line)];

    private static TransferLine Line(FileTransfer transfer)
    {
        var running = transfer.Status == TransferStatus.Running;
        var title = Title(transfer, running);
        switch (transfer.Status)
        {
            case TransferStatus.Running:
            {
                var (text, fraction) = transfer.Total is > 0 and var total
                    ? ($"{ByteFormat.Bytes(transfer.Done)} of {ByteFormat.Bytes(total)} ({Percent(transfer.Done, total)})",
                        Math.Clamp((double)transfer.Done / total, 0, 1))
                    : ($"{ByteFormat.Bytes(transfer.Done)} so far", (double?)null);
                return new TransferLine(transfer.Id, title, text, null, fraction, true, false, true, false, null);
            }

            case TransferStatus.Done:
            {
                var summary = transfer.Summary ?? TransferSummary.Empty;
                return new TransferLine(
                    transfer.Id, title, Sum(summary), Notes(summary), 1, false, false, false, true, transfer.IsDownload ? summary.Saved : null);
            }

            case TransferStatus.Cancelled:
                return Ended(transfer, title, transfer.IsDownload
                    ? "Cancelled. Nothing partial was left in the folder."
                    : "Cancelled. What the engine unpacked before the cancel stays in the container: an upload cannot be undone.");

            case TransferStatus.Interrupted:
                return Ended(transfer, title, "Stopped: the connection to the engine was lost.");

            default:
                return Ended(transfer, title, transfer.Error ?? "", isError: true);
        }
    }

    private static TransferLine Ended(FileTransfer transfer, string title, string text, bool isError = false) =>
        new(transfer.Id, title, text, null, null, false, isError, false, true, null);

    private static string Title(FileTransfer transfer, bool running)
    {
        switch (transfer.Request)
        {
            case DownloadRequest download:
            {
                var what = download.Sources.Length == 1 ? download.Sources[0].Name : $"{download.Sources.Length} items";
                return $"{(running ? "Downloading" : "Downloaded")} {what} to {download.Destination}";
            }

            case UploadRequest upload:
            {
                var what = upload.Sources.Length == 1 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(upload.Sources[0])) : $"{upload.Sources.Length} items";
                return $"{(running ? "Uploading" : "Uploaded")} {what} to {upload.Destination.Value}";
            }

            default:
                return "";
        }
    }

    /// <summary>"3 files and 1 folder, 4 MB."</summary>
    private static string Sum(TransferSummary summary)
    {
        var parts = Count(summary.Files, "file", "files");
        if (summary.Folders > 0)
            parts += " and " + Count(summary.Folders, "folder", "folders");
        return $"{parts}, {ByteFormat.Bytes(summary.Bytes)}.";
    }

    /// <summary>What was skipped and why, then what was renamed; null when nothing was.</summary>
    private static string? Notes(TransferSummary summary)
    {
        if (!summary.HasNotes)
            return null;

        var skipped = new List<string>();
        if (summary.Links > 0)
            skipped.Add(Count(summary.Links, "link", "links") + " (never created or followed)");
        if (summary.Special > 0)
            skipped.Add(Count(summary.Special, "device or FIFO", "devices or FIFOs"));
        if (summary.Unsafe > 0)
            skipped.Add(Count(summary.Unsafe, "path that would leave the folder", "paths that would leave the folder"));
        if (summary.Clashes > 0)
            skipped.Add(Count(summary.Clashes, "name that clashed on Windows", "names that clashed on Windows"));

        var notes = new List<string>();
        if (skipped.Count > 0)
            notes.Add("Skipped " + Join(skipped) + ".");
        if (summary.Renamed > 0)
            notes.Add($"Renamed {Count(summary.Renamed, "name", "names")} Windows cannot hold.");
        return string.Join(' ', notes);
    }

    private static string Join(List<string> items) =>
        items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    private static string Count(int count, string one, string many) =>
        count == 1 ? "1 " + one : count.ToString(CultureInfo.InvariantCulture) + " " + many;

    private static string Percent(long done, long total) =>
        ((int)Math.Floor(Math.Clamp((double)done / total, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture) + "%";
}

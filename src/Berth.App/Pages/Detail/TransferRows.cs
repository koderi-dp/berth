using System.Diagnostics;
using Berth.Components;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Detail;

/// <summary>The container whose transfers the panel shows.</summary>
internal sealed record TransferRowsProps(string ContainerId);

/// <summary>
/// The container's downloads and uploads under the Files table, newest first, as pulls are on the Images page. It follows the engine
/// state itself, so a transfer's progress (up to ten changes a second) renders only these rows, and each row only when its own line
/// changed.
/// </summary>
internal sealed class TransferRows : EngineStateComponent<TransferRowsProps>
{
    public override Element Render()
    {
        var state = UseEngineState();
        var transfers = state.Transfers;
        var containerId = Props.ContainerId;
        var lines = UseMemo(() => TransferView.Of(transfers, containerId), transfers, containerId);

        // An upload that ended (done, failed or cancelled) changed the container's files: the folder shown is read again, so its rows,
        // and the question before the next upload, see what is there now.
        var uploadsEnded = transfers.Values
            .Where(t => !t.IsDownload && t.Request.ContainerId == containerId && t.Status != TransferStatus.Running)
            .Sum(t => (long)t.Id);
        UseEffect(
            () =>
            {
                if (uploadsEnded > 0 && AppServices.Current.Detail is { } detail && detail.State.ContainerId == containerId
                    && detail.State.Files.Status != FilesStatus.Idle)
                    detail.RefreshFiles();
            },
            uploadsEnded);

        if (lines.IsEmpty)
            return Empty();

        // An upload that found same-named entries in the container waits here for the user's answer; nothing was sent yet.
        var asking = transfers.Values.Where(t => t.Status == TransferStatus.Asking && t.Request.ContainerId == containerId).MinBy(t => t.Id);
        var ended = lines.Count(l => l.CanDismiss);
        var header = ended == 0
            ? null
            : Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    Caption(lines.Length == 1 ? "1 transfer" : $"{lines.Length} transfers").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Grid(column: 0),
                    HyperlinkButton("Clear finished", onClick: ClearFinished).AutomationId("transfers-clear").Grid(column: 1))
                .Margin(12, 0, 8, 0);

        // About three rows; more scroll, so a long list never takes the table's room.
        var rows = ScrollViewer(
                (FlexColumn([.. lines.Select(line => Component<TransferRow, TransferRowProps>(new(line)).WithKey("transfer-" + line.Id))]) with
                {
                    RowGap = 0,
                }))
            // No horizontal scrolling: the rows take the panel's width, so the buttons at their end stay in view.
            .Set(viewer =>
            {
                viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                viewer.HorizontalScrollMode = ScrollMode.Disabled;
            })
            .MaxHeight(RowsMaxHeight);

        return (FlexColumn(header?.WithKey("header"), rows.WithKey("rows"), Question(asking).WithKey("question")) with { RowGap = 0 })
            .AutomationId("files-transfers");

        void ClearFinished()
        {
            foreach (var line in lines.Where(l => l.CanDismiss && !l.IsAsking))
                AppServices.Current.Session.Dispatch(new TransferDismissed(line.Id));
        }
    }

    /// <summary>The height of about three transfer rows.</summary>
    private const double RowsMaxHeight = 180;

    /// <summary>
    /// The replace question of an upload waiting for its answer; Cancel is the default. Replace sends it again with those names agreed
    /// to, and it asks the engine once more just before sending, so a name that appeared meanwhile is asked about too.
    /// </summary>
    private static Element Question(FileTransfer? asking)
    {
        var question = asking is { Request: UploadRequest upload } ? TransferConfirmations.Upload(asking.Asks.ToArray(), upload.Destination) : null;
        return ContentDialog(
            question?.Title ?? "",
            Body(question?.Text ?? "").TextWrapping().IsTextSelectionEnabled().AutomationId("confirm-text"),
            question?.Primary ?? "Replace") with
        {
            IsOpen = question is not null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            OnClosed = result =>
            {
                if (asking is not { Request: UploadRequest request })
                    return;
                var session = AppServices.Current.Session;
                session.Dispatch(new TransferDismissed(asking.Id));
                if (result == ContentDialogResult.Primary)
                    session.Dispatch(new TransferRequested(request with { Replace = [.. request.Replace, .. asking.Asks] }));
            },
        };
    }
}
internal sealed record TransferRowProps(TransferLine Line);

/// <summary>
/// One transfer: its glyph, what goes where, its progress line and bar, and Cancel while it runs; once ended, how it ended, what was left
/// out and why, "Show in folder" for a finished download, and Dismiss.
/// </summary>
internal sealed class TransferRow : Component<TransferRowProps>
{
    private static readonly GridSize[] Columns = [GridSize.Px(32), GridSize.Star(3), GridSize.Star(4), GridSize.Auto];
    private static readonly GridSize[] OneRow = [GridSize.Star()];

    public override Element Render()
    {
        var line = Props.Line;
        var id = line.Id;

        var text = Caption(line.Text)
            .TextWrapping(line.IsRunning ? TextWrapping.NoWrap : TextWrapping.Wrap)
            .TextTrimming(line.IsRunning ? TextTrimming.CharacterEllipsis : TextTrimming.None)
            .IsTextSelectionEnabled(!line.IsRunning)
            .Foreground(line.IsError ? Theme.SystemCritical : Theme.SecondaryText)
            .ToolTip(line.Text)
            .AutomationId("transfer-status-" + id)
            .LiveRegion(line.IsRunning ? AutomationLiveSetting.Off : AutomationLiveSetting.Polite);
        Element? notes = line.Notes is { } said
            ? Caption(said).TextWrapping(TextWrapping.Wrap).IsTextSelectionEnabled(true).Foreground(Theme.SecondaryText).AutomationId("transfer-notes-" + id)
            : null;
        Element? bar = line.IsRunning
            ? (line.Fraction is { } fraction ? Progress(100 * fraction) : ProgressIndeterminate()).AutomationName(line.Title)
            : null;
        var progress = DialogParts.Stack(4, text, notes, bar).VAlign(VerticalAlignment.Center).Margin(0, 8, 12, 8);

        var buttons = (FlexRow(
                line.CanCancel
                    ? Button("Cancel", () => Send(new TransferCancelRequested(id))).AutomationId("cancel-transfer-" + id).AutomationName("Cancel: " + line.Title)
                    : null,
                line.ShowInFolder is { } path
                    ? Button("Show in folder", () => ShowInFolder(path)).AutomationId("show-transfer-" + id)
                    : null,
                line.CanDismiss
                    ? Widgets.IconButton(Glyphs.Close, "Dismiss", () => Send(new TransferDismissed(id))).AutomationId("dismiss-transfer-" + id)
                    : null)
            with
            {
                AlignItems = FlexAlign.Center,
                JustifyContent = FlexJustify.FlexEnd,
                ColumnGap = 4,
            })
            .VAlign(VerticalAlignment.Center);

        return Border(
                Grid(
                    Columns,
                    OneRow,
                    Widgets.Glyph(line.IsDownload ? Glyphs.Download : Glyphs.Upload, size: 16, color: line.IsRunning ? Theme.AccentText : Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Left)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(column: 0),
                    Widgets.OneLine(Body(line.Title)).Foreground(Theme.PrimaryText).ToolTip(line.Title).VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0).Grid(column: 1),
                    progress.Grid(column: 2),
                    buttons.Grid(column: 3)).MinHeight(48).Padding(12, 0, 8, 0))
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 1, 0, 0)
            .AutomationId("transfer-" + id);
    }

    private static void Send(SessionEvent e) => AppServices.Current.Session.Dispatch(e);

    /// <summary>Opens Explorer on the folder that holds <paramref name="path"/>, with it selected. A Windows name never holds a quote.</summary>
    private static void ShowInFolder(string path)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            AppLog.Error("showing a download in its folder failed", ex);
        }
    }
}

using System.Collections.Immutable;
using Berth.Components;
using Berth.Files;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Detail;

/// <summary>What the Files tab does with its page's Escape, kept by the page and filled in by the tab while its path is edited.</summary>
internal sealed class FilesTabKeys
{
    /// <summary>Escape while the path is edited as text: leaves the edit. Null while Escape is the page's (back to the list).</summary>
    public Action? Escape { get; set; }
}

/// <summary>What the Files tab shows.</summary>
/// <param name="Files">The page's Files state, kept by the page while it is the same: a batch of log lines does not render the tab.</param>
/// <param name="Waiting">The page waits for the engine (<see cref="DetailPage.Waiting"/>).</param>
/// <param name="Connected">The engine is connected: transfers can start.</param>
/// <param name="StartPath">The folder the tab shows first: the last one shown on this page, or the root.</param>
internal sealed record FilesTabProps(string ContainerId, string Name, FilesState Files, bool Waiting, bool Connected, ContainerPath StartPath, FilesTabKeys Keys);

/// <summary>
/// The "Files" tab of a container's page: the folder's breadcrumb (which turns into a text box to type a path; Enter goes there), Up,
/// Refresh, Download… and Upload…, a note on how the folder was listed, the table, and the container's transfers under it. Downloads
/// ask for a folder and uploads for files or a folder (pickers owned by the window); both ask before they replace anything. Files dropped
/// from Explorer onto the table are uploaded into the folder shown. What it says is decided in <see cref="FilesView"/>,
/// <see cref="TransferConfirmations"/> and <see cref="TransferView"/>.
/// </summary>
internal sealed class FilesTab : Component<FilesTabProps>
{
    private static readonly GridSize[] ToolbarColumns = [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto];
    private static readonly GridSize[] OneRow = [GridSize.Px(36)];
    private const double CompactMaxWidth = 1100;
    private static readonly ListSort<FileColumn> ByName = new(FileColumn.Name, false);

    public override Element Render()
    {
        var props = Props;
        var files = props.Files;
        var compact = !UseBreakpoint(CompactMaxWidth);
        var (editing, setEditing) = UseState(false);
        var typed = UseRef("");
        var (selected, setSelected) = UseState(ImmutableArray<FileRow>.Empty);
        var (pending, setPending) = UseState<PendingTransfer?>(null);
        var handlers = UseMemo(() => new FilesTableHandlers(), Array.Empty<object>());
        var view = FilesView.Of(files, props.Waiting);
        var (sort, setSort) = UseState(ByName);
        var rows = UseMemo(() => files.Listing is { } listing ? FileRows.Build(listing, TimeZoneInfo.Local, sort) : ImmutableArray<FileRow>.Empty, files.Listing, sort);
        var crumbs = UseMemo(() => FilesView.Breadcrumb(files.Path).Select(c => Breadcrumb(c.Name, c.Path)).ToArray(), files.Path);
        var canAct = props.Connected && !props.Waiting;
        var listed = files.Status == FilesStatus.Loaded && files.Listing is not null;

        // The first listing, and again on a new session after the engine came back: the folder shown last on this page.
        UseEffect(
            () =>
            {
                if (files.Status == FilesStatus.Idle && !props.Waiting && Session(props.ContainerId) is { } session)
                    session.BrowseFiles(props.StartPath);
            },
            files.Status == FilesStatus.Idle,
            props.Waiting);

        // A new listing selects nothing.
        UseEffect(() => setSelected([]), rows);

        void Browse(ContainerPath path)
        {
            setEditing(false);
            Session(props.ContainerId)?.BrowseFiles(path);
        }

        void GoTo(string text)
        {
            if (ContainerPath.TryParse(text, files.Path, out var path))
                Browse(path);
        }

        props.Keys.Escape = editing ? () => setEditing(false) : null;
        UseEffect(() => () => props.Keys.Escape = null, Array.Empty<object>());

        handlers.Select = rows => setSelected(rows);
        handlers.SortBy = column => setSort(sort.Toggle(column));
        handlers.Open = row => Browse(row.Path);
        handlers.Up = () => Browse(files.Path.Parent);
        handlers.CopyPath = rows => ContainerParts.CopyToClipboard(string.Join("\n", rows.Select(r => r.Path.Value)));
        handlers.Download = rows => _ = DownloadAsync(rows.Select(r => r.Path).ToImmutableArray());

        async Task DownloadAsync(ImmutableArray<ContainerPath> sources)
        {
            if (sources.IsEmpty)
                return;
            var folder = await PickFolderAsync(new() { CommitButtonText = "Download here" });
            if (folder?.Path is { Length: > 0 } destination)
                StartDownload(sources, destination);
        }

        void StartDownload(ImmutableArray<ContainerPath> sources, string destination)
        {
            var session = AppServices.Current.Session;
            var question = TransferConfirmations.Download(sources.Select(s => s.Name).ToArray(), destination, p => File.Exists(p) || Directory.Exists(p));
            if (question is null)
                session.Dispatch(new TransferRequested(new DownloadRequest(props.ContainerId, sources, destination, Replace: false)));
            else
                setPending(new PendingTransfer(question, session, new DownloadRequest(props.ContainerId, sources, destination, Replace: true)));
        }

        async Task UploadAsync(bool folder)
        {
            string[] sources = folder
                ? (await PickFolderAsync(new() { CommitButtonText = "Upload" }))?.Path is { Length: > 0 } path ? [path] : []
                : (await PickMultipleFilesAsync(new() { CommitButtonText = "Upload" })).Select(f => f.Path).Where(p => p.Length > 0).ToArray();
            StartUpload(sources);
        }

        void StartUpload(IReadOnlyList<string> sources)
        {
            // The upload itself asks the engine about each name just before sending, and waits for a yes when one is there
            // (TransferRows shows the question): the listing on screen can be stale, capped or incomplete.
            if (sources.Count == 0 || files.Listing is not { } listing)
                return;
            AppServices.Current.Session.Dispatch(new TransferRequested(new UploadRequest(props.ContainerId, [.. sources], listing.Path)));
        }

        var toolbar = Toolbar(files, editing, typed, crumbs, canAct, listed, selected, compact, setEditing, Browse, GoTo, handlers, () => _ = UploadAsync(false), () => _ = UploadAsync(true));
        var table = Table(files, view, rows, compact, canAct, sort, handlers, StartUpload, listed);

        return (FlexColumn(
                toolbar.WithKey("toolbar"),
                Notes(view)?.WithKey("notes"),
                table.Flex(grow: 1, basis: 0).WithKey("table"),
                Component<TransferRows, TransferRowsProps>(new(props.ContainerId)).Flex(shrink: 0).WithKey("transfers"),
#if DEBUG
                DebugChannel(files, StartDownload, StartUpload, Browse).WithKey("debug"),
#endif
                Confirmation(pending, () => setPending(null)).WithKey("confirm"))
            with
            {
                RowGap = 8,
            })
            .Margin(0, 4, 0, 0)
            .AutomationId("files-tab");
    }

    /// <summary>The open detail session when it is this container's.</summary>
    private static ContainerDetailSession? Session(string containerId) =>
        AppServices.Current.Detail is { } session && session.State.ContainerId == containerId ? session : null;

    private static Element Toolbar(
        FilesState files,
        bool editing,
        Ref<string> typed,
        BreadcrumbBarItemData[] crumbs,
        bool canAct,
        bool listed,
        ImmutableArray<FileRow> selected,
        bool compact,
        Action<bool> setEditing,
        Action<ContainerPath> browse,
        Action<string> goTo,
        FilesTableHandlers handlers,
        Action uploadFiles,
        Action uploadFolder)
    {
        var up = Widgets.IconButton(Glyphs.Up, "Up to the folder above (Backspace)", () => browse(files.Path.Parent))
            .AutomationId("files-up")
            .IsEnabled(!files.Path.IsRoot && canAct);
        var refresh = Widgets.IconButton(Glyphs.Restart, "Refresh (F5)", () => browse(files.Path))
            .AutomationId("files-refresh")
            .IsEnabled(canAct);

        Element path;
        if (editing)
        {
            typed.Current = files.Path.Value;
            path = Widgets.TextField(files.Path.Value, text => typed.Current = text, "A folder in the container, such as /etc")
                .AutomationId("files-path-box")
                .AutomationName("Folder path")
                .OnMount(control => ((TextBox)control).Focus(FocusState.Programmatic))
                .OnKeyDown((_, e) =>
                {
                    if (e.Key == VirtualKey.Enter)
                    {
                        goTo(typed.Current);
                        e.Handled = true;
                    }
                })
                .OnLostFocus((_, _) => setEditing(false))
                .VAlign(VerticalAlignment.Center)
                .Margin(8, 0, 8, 0)
                .WithKey("path-box");
        }
        else
        {
            var bar = BreadcrumbBar(crumbs, item =>
                {
                    if (item.Tag is ContainerPath target)
                        browse(target);
                })
                .AutomationId("files-path")
                .AutomationName("Folder: " + files.Path.Value)
                .VAlign(VerticalAlignment.Center);
            path = Grid(
                    [GridSize.Star(), GridSize.Auto],
                    OneRow,
                    bar.Margin(8, 0, 4, 0).Grid(column: 0),
                    Widgets.IconButton(Glyphs.Edit, "Type a path", () => setEditing(true)).AutomationId("files-edit-path").Grid(column: 1))
                .WithKey("path-crumbs");
        }

        var download = Button(
                HStack(8, Widgets.Glyph(Glyphs.Download), compact ? Empty() : Body("Download…")),
                () => handlers.Download(selected))
            .AutomationId("files-download")
            .AutomationName("Download the selected items")
            .ToolTip("Download the selected items to a folder on this computer")
            .IsEnabled(canAct && !selected.IsEmpty);
        var upload = Button(HStack(8, Widgets.Glyph(Glyphs.Upload), compact ? Empty() : Body("Upload…")), null)
            .AutomationId("files-upload")
            .AutomationName("Upload into this folder")
            .ToolTip("Upload files or a folder from this computer into " + files.Path.Value)
            .WithFlyout(MenuItems(
            [
                MenuItem("Files…", uploadFiles) with { IconElement = Widgets.MenuGlyph(Glyphs.Document) },
                MenuItem("Folder…", uploadFolder) with { IconElement = Widgets.MenuGlyph(Glyphs.Folder) },
            ]))
            .IsEnabled(canAct && listed)
            .Margin(4, 0, 0, 0);

        return Grid(
                ToolbarColumns,
                OneRow,
                up.Grid(column: 0),
                refresh.Grid(column: 1),
                path.Grid(column: 2),
                download.Grid(column: 3),
                upload.Grid(column: 4))
            .Flex(shrink: 0);
    }

    /// <summary>How the folder was listed and what is not shown, or that the page waits for the engine.</summary>
    private static Element? Notes(FilesView view)
    {
        var lines = new[]
        {
            view.Waiting is { } waiting ? Caption(waiting).Foreground(Theme.SecondaryText).AutomationId("files-waiting") : null,
            view.WayNote is { } way ? Caption(way).Foreground(Theme.SecondaryText).AutomationId("files-way-note") : null,
            view.Note is { } note ? Caption(note).Foreground(Theme.SystemCaution).AutomationId("files-note") : null,
        };
        return lines.All(l => l is null) ? null : DialogParts.Stack(2, lines).Margin(8, 0, 0, 0).Flex(shrink: 0);
    }

    /// <summary>
    /// The table, or in its place a ring while the folder is first read, the engine's message with Retry when the read failed, or that
    /// the folder is empty. Files dropped from Explorer onto it are uploaded into the folder shown.
    /// </summary>
    private static Element Table(
        FilesState files, FilesView view, ImmutableArray<FileRow> rows, bool compact, bool canAct, ListSort<FileColumn> sort, FilesTableHandlers handlers, Action<IReadOnlyList<string>> upload, bool listed)
    {
        Element content =
            // A path that is not a folder has nothing to retry: Up goes back.
            view.Error is { } error ? Widgets.RetryBar(error, canAct && !files.NotAFolder ? Refresh : null).AutomationId("files-error")
            : view.Loading is { } loading ? ListTable.Loading(loading).AutomationId("files-loading")
            : view.Empty is { } empty ? ListTable.EmptyState(empty).AutomationId("files-empty")
            : Component<FilesTable, FilesTableProps>(new(rows, compact, canAct, sort, handlers));

        return Border(content)
            .OnDragOver(args =>
            {
                if (canAct && listed && args.Data.HasFormat(StandardDataFormats.StorageItems))
                {
                    args.AcceptedOperation = DragOperations.Copy;
                    args.UIOverride.Caption = "Upload into " + files.Path.Value;
                }
                else
                {
                    args.AcceptedOperation = DragOperations.None;
                }
            })
            .OnDrop(
                async args =>
                {
                    if (!canAct || !listed || !args.Data.HasFormat(StandardDataFormats.StorageItems))
                        return;
                    var items = await args.Data.GetFilesAsync();
                    upload(items.Select(i => i.Path).Where(p => p.Length > 0).ToArray());
                },
                DragOperations.Copy);
    }

    private static void Refresh() => AppServices.Current.Detail?.RefreshFiles();

    /// <summary>The question before a transfer replaces something; Cancel is the default. Nothing is sent when the connection changed since.</summary>
    private static Element Confirmation(PendingTransfer? pending, Action onDone) =>
        ContentDialog(
            pending?.Question.Title ?? "",
            Body(pending?.Question.Text ?? "").TextWrapping().IsTextSelectionEnabled().AutomationId("confirm-text"),
            pending?.Question.Primary ?? "Replace") with
        {
            IsOpen = pending is not null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            OnClosed = result =>
            {
                if (result == ContentDialogResult.Primary && pending is not null && ReferenceEquals(pending.Session, AppServices.Current.Session))
                    pending.Session.Dispatch(new TransferRequested(pending.Request));
                onDone();
            },
        };

#if DEBUG
    /// <summary>
    /// Debug builds only: a box and a button that start what a picker or a typed path would, so a check can drive the tab without a
    /// dialog or keys: <c>browse &lt;path&gt;</c>, <c>download &lt;container path&gt;[;…]|&lt;local folder&gt;</c> and
    /// <c>upload &lt;local path&gt;[;…]</c> (into the folder shown). Each goes through the same questions as the real thing.
    /// </summary>
    private static Element DebugChannel(
        FilesState files, Action<ImmutableArray<ContainerPath>, string> download, Action<IReadOnlyList<string>> upload, Action<ContainerPath> browse)
    {
        return HStack(
                8,
                Widgets.TextField("", text => _debugCommand = text).AutomationId("files-debug-command").Width(320),
                Button("Run", () => DebugRun(_debugCommand, files, download, upload, browse)).AutomationId("files-debug-run"))
            .Flex(shrink: 0);
    }

    private static string _debugCommand = "";

    private static void DebugRun(
        string command, FilesState files, Action<ImmutableArray<ContainerPath>, string> download, Action<IReadOnlyList<string>> upload, Action<ContainerPath> browse)
    {
        var space = command.IndexOf(' ');
        if (space < 0)
            return;
        var verb = command[..space];
        var rest = command[(space + 1)..];
        switch (verb)
        {
            case "browse" when ContainerPath.TryParse(rest, files.Path, out var path):
                browse(path);
                break;
            case "download" when rest.Split('|') is [var sources, var folder]:
                // A source that is empty or does not parse is dropped, never taken for the folder shown.
                var paths = sources.Split(';').Where(s => s.Trim().Length > 0)
                    .Select(s => ContainerPath.TryParse(s, files.Path, out var p) ? p : (ContainerPath?)null)
                    .OfType<ContainerPath>()
                    .ToImmutableArray();
                if (!paths.IsEmpty)
                    download(paths, folder);
                break;
            case "upload":
                upload(rest.Split(';'));
                break;
        }
    }
#endif

    /// <summary>A transfer waiting for the user's yes: the question, the session it was asked on, and what to send.</summary>
    private sealed record PendingTransfer(TransferConfirmation Question, EngineSession Session, FileTransferRequest Request);
}

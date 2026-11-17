# Berth

A Windows desktop GUI for a Docker engine (containers, images, volumes, networks; local
and remote engines). UI is pure C# with
[Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor)
(`0.1.0-preview.18`) on WinUI 3, .NET 11. There is no XAML: do not add `.xaml` files.

All seven phases are built: the shell, connections (TCP, TLS, named pipe), the engine client,
the session, the Containers and Settings pages (phase 1), a container's detail page with
Logs, Inspect, Bind mounts and Stats tabs plus live CPU and memory on the list (phase 2),
the Images, Volumes and Networks pages with pulls, the run dialog, image inspect, volume
create, a volume's page and removes (phase 3), and the Exec tab: a terminal (xterm.js in a
WebView2) on a shell in the container, the shell picker, "Open terminal" on a row, "Open in
external terminal" and "Copy command" (phase 4), engine control (start and stop commands a
connection stores), the tray icon and its menu, closing to the tray, one instance and start with
Windows (phase 5), and the Files tab: browse a container's files, download and upload through the engine's archive (tar)
calls (phase 6), and SSH connections: each stream a `docker system dial-stdio` on an SSH session, sign-in by password or key
file, secrets in Windows Credential Manager, the host key question and the changed-key block (phase 7; see "SSH").

## Layout

```
Berth.slnx                  the solution: /src/ (Berth.App, Berth.Core), /tests/
src/Berth.Core/             plain net11.0 library, no WinUI/Reactor reference; pure logic, unit-tested (root namespace Berth)
  Connections/              ConnectionProfile, EngineAddress (tcp://, npipe:// and ssh://user@host:port parsing), ConnectionDraft (editor logic),
                            SshOptions (user, sign-in, key file, accepted HostKeys; Check/Accept/Remove: a changed key is never
                            accepted in place), SshProblem (why an SSH connection failed, SshFailure, its message; StopsRetry),
                            Secrets (ISecretStore, SecretKind: the password and passphrase by connection id),
                            EngineControl (a connection's start and stop commands, the WSL default), CommandLine (a
                            command line split as the Windows C runtime splits it; here, not in Text, because the draft
                            checks it, and the Exec tab's ExecCommand splits for a Linux shell, where "\" escapes)
  Processes/                CommandRunner (a program run with no shell and no window, the end of its output kept, its
                            process tree ended on cancel), OutputTail, ConsoleText (UTF-16 or UTF-8 console output),
                            WslDistros (`wsl.exe -l -q` parsed)
  Settings/                 AppSettings, SettingsStore (atomic save, damaged-file handling, first-run save), FirstRun (the
                            settings when there are none), SettingsFile (the tolerant read shape and its checks),
                            SettingsJson, ConnectionEdits
  Transport/                IEngineTransport, TcpTransport (plain and TLS), NamedPipeTransport, TransportFactory, IHalfClosable;
                            SshTransport, SshChannelStream, SshTransportException, SshDialFailure (see "SSH"; the only files that
                            may use SSH.NET)
  Engine/                   EngineClient (IEngineApi): version negotiation, info, list (with each container's mounts and
                            network names), actions, create, event stream, inspect, logs (StreamLogsAsync), stats
                            (StreamStatsAsync); the archive calls and the non-TTY exec that returns its output (EngineClient.Archive:
                            StatPathAsync, GetArchiveAsync, PutArchiveAsync with Expect: 100-continue, RunExecForOutputAsync); exec: create (a TTY exec with Berth's detach keys, ExecDetachKeys), start
                            on a connection switched to a two-way raw stream (HttpUpgrade writes the upgrade request by
                            hand on the transport's own stream; ExecStream/UpgradedExecStream, whose write side can be
                            closed alone on TCP and TLS), resize, inspect, and a detached run (RunDetachedExecAsync); images (list, inspect with the raw answer, remove, pull as a stream of PullProgress),
                            volumes (list, create, remove), networks (list, remove), disk usage (/system/df for images and
                            volumes). Image and volume removes are never forced, and the engine's prune endpoints are not
                            called: "Remove unused …" removes item by item. Pulls, disk usage and streams have no deadline;
                            other calls 30 s (stop, restart and image and volume removes 90 s).
                            EngineHttp, EngineJson (source-generated), ApiVersion, ConnectionTester, EngineTime, JsonLines
                            (line-delimited JSON streams), LogFrames (the engine's multiplexed log framing), LogLines (frames
                            or TTY text to LogLine), AnsiText (terminal output to plain text and colour spans), StatsMath (CPU and memory from
                            a raw stats line), ImageReference (a pull reference split into name and tag or digest, as the
                            docker CLI does), DiskUsageMapping (/system/df to DiskUsage: the engine's totals from API 1.52,
                            computed as the CLI does before),
                            Models/ (ContainerSummary, ContainerDetails/ContainerInspection, ContainerCreateRequest/
                            PortBinding, ImageSummary/ImageDetails/ImageDeleteResult/PullProgress, VolumeSummary,
                            NetworkSummary, DiskUsage, LogLine, StatsSample, Exec: TerminalSize, ExecInspection)
  Files/                    the Files tab's container side (see "The Files tab"): ContainerPath (a POSIX path, never above /),
                            ArchiveReader (a streaming tar reader on System.Formats.Tar; sparse refused), ArchiveWriter (an upload's
                            tar), ArchiveExtractor (a download saved by the safety rules), WindowsNames, TransferSummary, ExecListing and
                            ArchiveListing (the two ways to list a folder), ListingChoice (which one, and the fall-back), ArchiveTransfer
                            (a download or an upload against IEngineApi)
  Text/                     text made from engine records for the user to paste or run: DockerRunCommand (the `docker run`
                            command of an inspected container, PowerShell or bash quoting; ShellKind), ShellQuote,
                            DockerExecCommand (the `docker -H ... exec -it` command for a connection, as an argument list
                            and as PowerShell text), ExternalTerminal (how "Open in external terminal" starts it: a
                            Windows Terminal tab or a console, as data, with what each refuses and why)
  State/                    value types that more than one layer holds or reads: LogBuffer (last 10,000 lines),
                            StatsHistory (last 120 samples, as chart series), ContainerSort (the list's saved sort choice),
                            ResourceList/ListStatus/RemovalBatch (the images, volumes and networks lists and their
                            batches of removes), ImagePull (a pull's layers), DiskUsageState, ContainerRun (the run
                            dialog's request and how it ended), ImageInspect (the image read for the run dialog and
                            Inspect), VolumeCreate (the create volume dialog's request and how it ended; a dialog reads
                            only the entry with its own request number, which the session always answers), InUse (the
                            one rule for what a container uses)
  Session/                  EngineState, SessionEvents, SessionEffects, SessionReducer (all list behaviour, including list
                            stats), with ResourceEvents, ResourceEffects and SessionReducer.Resources for images, volumes,
                            networks, disk use, pulls, running a container and reading an image; EngineSession (runs the reducer and its
                            effects; reports failures to an optional error sink; opens detail sessions on its current
                            connection), its parts in EngineSession.Connection, .StatsWatch, .Resources and .Pulls (progress
                            batched to ten state changes a second per pull), Backoff;
                            DetailState, DetailEvents, DetailReducer (all detail page behaviour), ContainerDetailSession
                            (runs the detail reducer: inspect, log and stats streams of one container); the shell:
                            ExecState (its status and command, never its bytes), DetailReducer.Exec, ContainerDetailSession.Exec (the stream's
                            owner and both pumps), ExecInput (the 2 MiB input cap), IExecOutput (the terminal's end, which
                            the app attaches), ExecCleanUp (the BERTH_EXEC marker and the clean-up command); the Files tab:
                            FilesState (DetailState.Files), DetailReducer.Files, ContainerDetailSession.Files (runs a listing);
                            transfers: FileTransfers (EngineState.Transfers, by ticket), TransferEvents, SessionReducer.Transfers,
                            EngineSession.Transfers (progress batched to ten state changes a second per transfer)
  Presentation/             ContainerRows, ComposeGrouping, ContainerFilter, ContainerSorting, ContainerStatus, BulkSelection,
                            RowActions (a container's, image's, volume's or network's row actions), EngineCounts, AgeFormat, PortFormat, ByteFormat; RowSelection (checked rows of any
                            list: ISelectableRow, what a bulk action and a confirmed one send), ListSort (the images, volumes
                            and networks tables' sort); ImageRows, VolumeRows, NetworkRows (rows, filters, sort, empty texts),
                            Confirmations (what a delete or "Remove unused" sends, item by item, and its dialog text; "Remove
                            unused" waits for the container list and its own list: UnusedNotKnown), BackTarget (where a container's page
                            goes back to and its button's label), RunOutcome (the run dialog's line), PullFigure (a pull's progress
                            line), PullReference (the pull dialog's field check), RunDraft (the run dialog's fields, checks
                            and create request); detail page: LogView, LogSearch,
                            InspectSections, StatsCharts (axes, figures, screen-reader summaries), StatsSummary (list strip
                            and CPU column texts), DetailPage (the detail page's lifecycle: when it opens, closes, refreshes
                            or leaves its session, and which state its tabs show), DetailTabs (the tabs in order); the
                            terminal: ExecView (what the Exec tab shows and says for each state), ExecCommand (the shell
                            choices and a custom command's parse), ExecInputNotes, TerminalProtocol (the page's messages),
                            TerminalFlow (output in flight), TerminalScreen (which shell's output is shown), TerminalTheme
                            (the sixteen colours), TerminalRecovery (what a failed browser process does), ShortcutFocus
                            (when the window's shortcuts give way to the terminal); the Files tab: FilesView (rows, notes,
                            breadcrumb; TransferConfirmations, the questions before replacing), TransferView (a transfer's line)
  Logging/, Storage/        FileLog, AtomicFile
src/Berth.App/              UI and wiring (packaged single-project MSIX app, assembly Berth, root namespace Berth)
  Program.cs                entry point: log, services, first connect, window; a Release build first removes every
                            WEBVIEW2_* environment variable
  DebugStart.cs             Debug only: BERTH_OPEN_EXEC and BERTH_EXEC_COMMAND (see "The exec terminal")
  AppLog.cs                 berth.log: fixed wording and exception type names only (FileLog.Describe)
  LocalData.cs              the folder for settings and the log (package LocalState, or %LOCALAPPDATA%\Berth unpackaged)
  CredentialStore.cs        ISecretStore over Windows Credential Manager (CredRead/CredWrite/CredDelete, generic credentials
                            `Koderi.Berth/<id>/password|passphrase`, persisted for this user on this computer)
  AppSecrets.cs             the app's secret store: Credential Manager, and a secret entered without "Remember" for one attempt
  AppServices.cs            settings, the session for the active connection (replaced on a switch) and the detail session of
                            the open container page (OpenDetail, CloseDetail, DetailChanged); sends session failures and a
                            set-aside settings file to the log
  EngineConnection.cs       an EngineClient plus the transport it runs on, disposed together
  EngineCommands.cs         runs a connection's engine commands for its session (IEngineControl over CommandRunner);
                            logs fixed words and the exit code only
  WslList.cs                asks wsl.exe for the distributions (read only) for the editor's WSL default
  Shell/                    the window outside its pages (see "The tray and the window"): TrayIcon, TrayIcons,
                            TrayFlyout, TrayFlyoutWindow, MainWindow, SingleInstance, StartWithWindows, Notifications
  App.cs                    the window component: title bar, navigation (AppRoute: Containers, Images, Volumes, Networks,
                            ContainerDetail, VolumeDetail, Settings), status bar; switches list stats on and off (see below) and tells the
                            session which of the images, volumes and networks lists is wanted when its page is shown
  Hooks/                    EngineStateHook.cs (EngineStateComponent and EngineStateComponent<TProps>, the base classes
                            for components that show the engine state), DetailStateHook.cs (the open detail session's state as an external store),
                            UiThreadSubscription.cs (shared by both: follows a replaceable source and coalesces its
                            thread-pool changes into one pending UI-thread callback), SettingsHook.cs (settings as an
                            external store), ListViewHook.cs (ListPageComponent, the base class of the four list pages:
                            UseListView keeps a list's search, filter, sort and collapsed groups by route in
                            ListViewStore, so a page the navigation cache dropped starts again from them; UseListSearch;
                            UseCheckedKeys; UseRemoveDialogs, the page's delete and "Remove unused …" confirmations)
  Components/               ConnectionSwitcher, StatusBar, DisconnectedView (no list yet), ConnectionBar (disconnected
                            above a kept list), SshPromptButton (the SSH host key question, changed-key warning and secret prompt
                            from those two), ErrorBar, Widgets (glyphs, text styles, TextField and SearchBox, OneLine,
                            RetryBar), ContainerParts (action buttons, menus, state mark and port list shared by a list
                            row and the detail header), ListPage and ListTable (the list scaffold, below), shared by
                            the four lists;
                            ShellKeys (the app's shortcuts at the window root, Ctrl+F, F5, Delete, Escape, Alt+Left,
                            acting for the page shown through the PageKeys it registers), RemovalPanel (a list's batch of removes:
                            progress, then what was removed, refused or skipped), RemoveDialog (the confirmation of a delete
                            or "Remove unused …" of images, volumes or networks, planned again at confirm, and what a
                            confirmed one sends), DialogParts (a primary button that keeps
                            its dialog open, a wider dialog, stacks, field errors and hints), DeleteDialog, DetailHeader
                            (breadcrumb, name, ID, image, ports, status, actions), StatsStrip (total CPU and memory
                            above the list)
  Pages/                    ContainersPage, ContainerRowView, ImagesPage, VolumesPage, NetworksPage, SettingsPage,
                            ConnectionEditor, ContainerDetailPage (header, tab strip, keeps a detail session open while
                            shown), VolumeDetailPage (header and the containers that mount it, from the session's lists)
    Images/                 ImageTable (the images table, a component with record props so a pull's progress does not
                            build it again), PullRows (the pulls under way, at the top of the table), PullDialog, RunDialog
                            (run a container from an image), InspectDialog (an image's raw JSON)
    Volumes/                VolumeTable (the volumes table, a component with record props), CreateVolumeDialog
    Networks/               NetworkTable (the networks table: attached containers as a menu of links, no Delete for the
                            built-in networks)
    Detail/                 LogsTab (follow, search, timestamps, wrap, colours, copy, clear), InspectTab (sections and raw JSON),
                            BindMountsTab, StatsTab (four charts), StatsChart (one chart card)
    Detail/ (terminal)      ExecTab (toolbar with the shell picker, size, external terminal and copy; status line; the
                            terminal; placeholders), ExecLink (the tab's link to the detail session, the handle and the
                            bridge, with stable handlers), TerminalView (the WebView2 host component and its
                            TerminalHandle), TerminalBridge (bytes from the session's pump to the page, with flow
                            control), DetailTabRequest (a row's "Open terminal" asks the page for its Exec tab)
    Detail/ (files)         FilesTab (toolbar, breadcrumb or typed path, notes, pickers, drop target, the replace question, and in
                            Debug the files-debug-command box), FilesTable (the table, selection and keys), TransferRows (the
                            container's transfers under the table)
  Assets/Terminal/          the terminal page: index.html, terminal.js, terminal.css, and xterm.js 6.0.0, its fit addon
                            0.11.0 and its licence, vendored unchanged (THIRD-PARTY-NOTICES.txt at the root)
  Package.appxmanifest      package identity Koderi.Berth, publisher CN=Koderi
tests/Berth.Core.Tests/     xunit tests for Core; Integration/ holds the tests that need a real engine
```

Core's folders depend one way. Each folder may reference only the folders in its row; one
assembly gives no compiler check, so `tests/Berth.Core.Tests/LayerTests.cs` holds the same
table, reads the source and fails on any other reference. It also fails when a folder is not in
the table or a file's namespace is not its folder's.

| Folder | May reference |
|---|---|
| `Connections` | nothing |
| `Transport` | `Connections` |
| `Engine` | `Connections`, `Transport` |
| `Text` | `Connections` (the connection a `docker` command names with `-H` and its TLS files), `Engine` (the model records it formats) |
| `State` | `Engine` |
| `Files` | `Engine` |
| `Session` | `Connections` (an SSH connection's `SshProblem`), `Engine`, `State`, `Text`, `Files` |
| `Presentation` | `Connections` (host keys and SSH problems it words), `Engine`, `State`, `Session`, `Text`, `Files` |
| `Settings` | `Connections`, `State`, `Storage` |
| `Logging` | nothing |
| `Storage` | nothing |
| `Processes` | nothing |

Text the engine client needs itself (`AnsiText`) lives in `Engine`, since `Engine` may not use
`Text`. `Presentation` uses only `Session`'s plain records: the state (`EngineState`, `DetailState`
and what they hold) and the events it builds for the app to dispatch (a confirmed remove's
`RemoveRequested`); never the runtime. The test cannot tell those apart, so a review must. A type that a
lower folder needs moves down (to `Engine`, `Text` or `State`), never the reference up: the
saved sort choice is `State.ContainerSort`, and how the table sorts by it is
`Presentation.ContainerSorting`.

Pure logic goes in Core and is unit-tested; the app project holds UI, interop and wiring only.
Where the original plan put logic in the app, it now lives in Core: the connection editor's
rules are `ConnectionDraft`, the settings page's list changes are `ConnectionEdits`, and the
containers page's checked-row logic and whether a row, project or bulk button can act (not while
disconnected, not on a row with an action in progress) is `BulkSelection`. The engine state reaches components
through the `EngineStateComponent` base class, not a hook function (Reactor keeps a class
component's hooks protected).

## The reducer rule

All session behaviour is in `SessionReducer.Reduce(state, event) -> (state, effects)`.
`EngineSession` only runs it: it feeds events through the reducer on one loop and carries
out the effects. New list behaviour is a new event or effect plus a reducer test in
`tests/Berth.Core.Tests/Session/SessionReducerTests.cs`, never state kept in a component.
Components read `EngineState` and dispatch events; they hold only view state (search text,
checked rows, an open dialog).

The detail page follows the same rule with its own pair: `DetailReducer.Reduce(DetailState,
DetailEvent) -> (state, effects)` holds all its behaviour (tests in
`tests/Berth.Core.Tests/Session/DetailReducerTests.cs`), and `ContainerDetailSession` only
runs it.

## The list scaffold

The Images, Volumes and Networks pages (and Containers, for the parts it uses) are one shape,
built from shared pieces; a new list uses them rather than copying a page:

- Each route's page is one `RoutePage` component (`App.cs`), and a list or volume page stays
  mounted, hidden under the disconnected view, instead of being swapped for it: Reactor's
  navigation host kept every page whose root element changed type alive, and the components in
  an unmounted virtualized list stayed registered, so each switch of connection leaked the old
  page (about 17 MB). A page therefore outlives a switch, and drops what it holds about the old
  engine (checked rows, open dialogs, notes) through `ListPageComponent.UseSessionReset`.

- `ListPageComponent` (`Hooks/ListViewHook.cs`): `UseListView` (search, filter and sort kept by
  route), `UseListSearch`, `UseCheckedKeys` (a row that is gone cannot stay checked) and
  `UseRemoveDialogs`.
- `ListPage.Render(title, state, summary, toolbar, bars, table, dialog)`: the sections in order.
  `ListPage.UsageLine`, `ListPage.Toolbar` (search box, filter, a primary action and secondary
  actions that go into one "more" menu below 1300 effective px), `ListPage.Bars` (the batch
  panel, the bulk bar), `ListPage.Keys` (Ctrl+F, F5, Delete for the page's `PageKeys`).
- `ListTable`: columns with wide and compact widths, the header with sortable titles, rows, the
  virtualized table, and `ListTable.States` (loading, failed first read with Retry, or the
  empty text in the table's place). Each table is a component with record props and a
  `TableHandlers` object kept by the page, so an unchanged table is not built again.
- Removing: a page gives `UseRemoveDialogs` a function that plans a delete from the rows it
  shows (`Confirmations.Remove*` over `*Rows.Shown(...)`). `RemoveDialog.For` plans a
  confirmed delete again from the session as it is then, and a confirmed "Remove unused …"
  keeps only what it named that is still unused (`Confirmations.ConfirmPrune`); a volume's
  page has its own small copy of the delete (`VolumeDetailPage.ConfirmDelete`, the same plan
  from the one row). Nothing is sent when the connection was switched since the dialog opened
  (`RemoveDialog.IsCurrent`). The session sends a batch a few items at a time, never forced,
  and skips an image whose tags are not the ones its confirmation saw; `RemovalPanel` shows
  its progress and result.

## The detail session

`ContainerDetailSession` owns one container page: `Open()` inspects the container, then
follows its log (the last 1000 lines, then new ones) and, while it runs, its stats.
`Refresh()` inspects again and restarts the streams when the container was started again
since the last read, or when it runs while a stream of it has ended (a restart the read after
the stream ended saw only half of; that read itself never restarts them, so a stream that keeps
ending cannot loop). Results carry the generation of the inspect or stream that asked for
them and the reducer drops older ones, so a stream stopped on purpose never reaches the page
and is never reported as an error. Log lines are batched and applied at most every 100 ms
(`LogTick`), so a flooding container is ten state changes a second, not one per line. When a
running container's stream ends by itself the reducer inspects again, so the page shows the
new state (or that the container was removed).

The session is opened through `EngineSession.OpenDetail` (via `AppServices.OpenDetail`) so
it runs on the engine session's current connection; it never disposes that client. When the
connection is retired the engine session cuts the detail session off (`CutOffAsync`: calls
stopped, state `ConnectionLost`), and the page opens a new one on the new connection.
`App` closes the detail session whenever the page shown is not a detail page, and removes
every detail route from the history on a switch of connection. Components reach the state
through `DetailStateHook` (an external store). The page's lifecycle is decided in Core by
`DetailPage.Next` (open, close, refresh or leave; tests in `DetailPageTests`), which the page
only carries out: it refreshes on each list load that shows the container may have run again
(another state or status text, an action ended, or the session out of step with the row), so a
restart from outside Berth too quick for the list to show is still followed. While the session
is closed, reopening or its connection is lost, the tabs show the last live state and say
"Waiting for the engine…" instead of what their streams say (`DetailPage.Shown`, `Waiting`).
Both `DetailStateHook` and `EngineStateHook` use
`UiThreadSubscription`, which follows a source that can be replaced and coalesces its
thread-pool changes into at most one pending UI-thread callback that reads the latest state.

## List stats

The Containers list shows each running container's CPU (a sortable `CPU (%)` column in the
wide layout) and the totals above it (`StatsStrip`). `EngineSession` follows the stats of
every running container only while `EngineState.StatsWanted` is true. `App`, not the page,
sends `StatsWantedChanged(route == Containers && !minimized)` and sends it again to each new
session after a switch: the list page stays in the navigation cache and does not render
while another page shows, so it cannot see the route change itself. Minimized is read from
Win32 `IsIconic`, because Reactor's window state does not report the restore.

## Engine control

A connection may carry `EngineControl`: a start and a stop command line. `SessionReducer.EngineControl` decides everything
(one command at a time, a 60 s limit, quick reconnects after a start, no reconnects after a stop until Start engine or Retry,
`EngineControlState.StoppedByBerth`); `EngineSession` runs the command through `IEngineControl`, which the app gives each
session (`EngineCommands`). Commands run with no shell (`CommandRunner`: `UseShellExecute=false`, no window, standard input
closed, the last 20 lines kept, the process tree ended on timeout, returning when the program exits even if a program it
started holds the output), in the user profile folder (so a bare program name is never found in whatever folder Berth was
started from; it is looked up in Berth's folder, System32, Windows and PATH). A `.bat` or `.cmd` program is refused
(`CommandLine.BatchFile`): Windows would run it through cmd.exe, which acts on `&`, `|` and `%`; `cmd.exe /c file.bat` runs
it with the shell in plain sight. Neither a command line nor its output is ever logged: fixed words and the exit code only.

A command belongs to the connection it was started for: it runs to its end or its 60 s limit even when its session is
replaced (a switch, an edit) or Berth quits, and a replaced session drops its result. The editor refuses to save the active
connection while its command runs ("Wait for the engine command to finish."). Reconnects pause while a stop runs. An edit of
a connection whose engine Berth stopped keeps it stopped when the address is unchanged (`EngineStoppedCarriedOver`); an
address change starts over. A stop question or a tray action sends nothing when the connection was switched since it opened.

**Never stop, restart or reconfigure a real engine in a check** (the daily engine, Docker Desktop's). Check engine control
with a stand-in: a small TCP forwarder (a spare local port to `127.0.0.1:2375`) added as a connection whose start command
launches the forwarder and returns, and whose stop command ends it. A failing command (`powershell.exe -NoProfile -Command
"Write-Output 'no'; exit 3"`) and a slow one (`... Start-Sleep -Seconds 120`) check the failure and the timeout. Remove the
stand-in connection and end the forwarder afterwards. Reading the WSL side is allowed (`wsl.exe -l -v`, `systemctl
is-active|is-enabled|cat`); nothing that changes it.

## SSH

**Where it lives.** SSH.NET (`2026.0.1`, MIT) is referenced by `Berth.Core` and used only by `Transport/Ssh*.cs` (`LayerTests` fails on
any other file, in Core or the app, that names `Renci.SshNet`, and on the package in the app's project). Everything else sees
`IEngineTransport` streams and `SshProblem`. The trimmed Release publish needs no trimming roots for it (checked by running a fully
trimmed build against the stand-in host).

**The transport** (`SshTransport`): one SSH session opened on the first stream and kept; each stream is one `docker system dial-stdio`
exec channel (`SshTransport.DialCommand`, fixed text: no user text ever goes into the remote command), its stdin and stdout as the
stream, stderr kept apart and read only when the command fails (`SshDialFailure`: no docker CLI, docker refused, dial-stdio's own
error, the remote's words as-is, bounded to 2000 characters). Its EOF is the exec stream's close-write (`IHalfClosable`). A cancelled
read ends the stream (SSH.NET's reads ignore the token). Up to 10 streams per session (`ChannelsPerSession`, sshd's default
`MaxSessions`); a channel the server refuses marks the session full at what it holds; either way the next stream opens another
session, up to 8; a second session closes with its last stream. Keep-alive every 15 s; a session that fails ends its streams with
`SshFailure.Dropped`, and the session reconnects as for any lost connection (a new transport, the same accepted key and kept secret).

**Host keys.** A host and port with no accepted key is only probed: the key exchange runs, the key is refused, and
`HostKeyUnknown` carries it (nothing else reaches the host: SSH.NET sends the user name only in its sign-in requests). With a key
accepted, a different one (type or fingerprint) is refused before sign-in (`HostKeyChanged`, both keys). Accepted keys live in
`settings.json` on the connection (`SshOptions.KnownHosts`, per host and port). `SshOptions.Accept` and
`ConnectionEdits.AcceptHostKey` never put a changed key in place of the saved one; the user removes the saved key in the editor
first. No "trust all" anywhere.

**Secrets.** A password or passphrase lives only in Windows Credential Manager (`CredentialStore`) and in memory while a connection
needs it (the transport keeps it until disposed, for a second session). Never in `settings.json`, the log, an exception message
Berth writes, a command line, a screenshot. The transport reads it only once the host key is known; none kept is `SecretMissing`
(no connection made). A refusal is `SignInRefused` and is never retried on its own: the reducer keeps it as `EngineState.Attention`
(no `ScheduleRetryEffect`, `RetryDue` ignored) until the user enters a secret or a new session starts; the disconnected view offers
the prompt instead of Retry. Deleting a connection deletes both its secrets (`AppServices.DeleteConnection`).

**Session and UI.** `ConnectFailed`, `ConnectionLost` and `LoadFailed` carry the `SshProblem` from `EngineException.Ssh`; one that
`StopsRetry` (unknown or changed key, missing or refused secret) sets `EngineState.Attention`, phase `NeedsAttention` ("Needs
attention" in the status bar and the tray, which never asks). `SshPromptButton` (disconnected view and bar, `ssh-prompt`) opens the
question (Accept saves the key through `AppServices.AcceptHostKey`, which replaces the session, which connects), the changed-key
warning (Close only), or the secret prompt (`ssh-secret`, `ssh-remember`; Connect calls `AppServices.EnterSecret`). Engine control is
off for SSH (the editor says so; `SettingsFile` drops it). The editor: `ssh-sign-in`, `ssh-key-path`, `ssh-key-browse`, `ssh-secret`,
`ssh-keys` with `remove-host-key-<n>`, `test-connection`, `test-accept-key`, `engine-control-ssh`. The Exec tab's external command
is `docker -H ssh://user@host:port exec -it …` with the note `exec-external-note`.

**The stand-in host for checks.** There is no real remote machine and no engine may be stopped or reconfigured, so checks use a
throwaway SSH server container on the daily engine: name `berth-check-ssh`, image `berth-check/ssh:test` (alpine 3.20 with
openssh-server and docker-cli; `berth-check/ssh:nocli` without the CLI), users `berth` (in the socket's group, in the container's own
`/etc/group`; the host's socket is never changed) and `nodocker`, a throwaway password and two throwaway ed25519 key pairs (one with
a passphrase) made for the check, new host keys each time the container is created (`ssh-keygen -A` in the entrypoint; recreate it
to show a changed key), `MaxSessions` from `-e MAX_SESSIONS=n`, `-v /var/run/docker.sock:/var/run/docker.sock`, port
`-p 127.0.0.1:2222:22` only. Build context and keys under `%TEMP%\berth-check\ssh\`, removed afterwards with the container, the
images, the test connections and their credentials (`cmdkey /list | findstr Koderi.Berth`). The owner's own hosts, keys, passwords
and `%USERPROFILE%\.ssh` are never used. The integration tests build their own copy (`SshStandIn`, `berth-it-*` names).

## The tray and the window

- `Shell/TrayIcon` owns the notification area icon through Reactor's own tray icon (`ReactorApp.OpenTrayIcon`;
  Shell_NotifyIcon underneath, works trimmed). Its icon and tooltip, and the menu, come from `Presentation/TrayMenu`, a pure
  function of the state whose instances compare equal when nothing shown changed: the icon is updated, and the menu built
  again, only then. It follows the session directly (not through the hooks), so it stays right while the window is hidden.
  It is removed in `Program`'s window-closed handler, so Quit leaves no ghost icon.
- The menu (`TrayFlyout`) is a Reactor component in Reactor's tray flyout window; `TrayFlyoutWindow` fits that window to the
  menu (Reactor makes it a fixed size in physical pixels). Container actions from it go through the session like the list's;
  a failure shows in the error bar and as a notification (`Notifications`). A stop of the engine asks first in the menu.
- `Shell/MainWindow`: closing the window hides it to the tray only for the user's close (`WindowCloseReason.UserClosed`, and
  not after `WM_QUERYENDSESSION`: shutdown, sign-out and the Restart Manager before a package update close it for real),
  while "Keep running in the tray" is on **and the tray icon is open** (a hidden window with no icon would strand Berth).
  Start with Windows is read again when Settings is shown and when the window is activated. Quit closes the window for good; the exit path (a shell ended
  within one second, the icon removed) is the same as before.
- One instance: `SingleInstance.Claim` (Windows App SDK `AppInstance` redirection) runs first in `Program`; a second launch
  hands its activation to the first, which shows its window, and exits. `dotnet run` re-registers the package and ends a
  running Berth, so check a second launch with `explorer.exe shell:AppsFolder\Koderi.Berth_<id>!App`.
- Start with Windows: the `windows.startupTask` extension (`BerthStartup`) in `Package.appxmanifest`, read and set by
  `StartWithWindows`; started that way (`ExtendedActivationKind.StartupTask`) the window starts hidden.
- **A hidden window does no rendering work.** `UiThreadSubscription` holds back the engine and detail state changes while
  `MainWindow.IsHidden` and delivers the latest once the window shows; `App` treats hidden like minimized (no list stats).
  Anything new that renders from a store must go through it, and anything that must stay live while hidden (the tray) must
  not.
- Checks without a mouse: the tray icon's callbacks can be posted to Reactor's `ReactorTrayHiddenWindow` (a message-only
  window of the Berth process) as Explorer sends them: message `WM_APP+1`, lParam low word `WM_CONTEXTMENU` (right click) or
  `NIN_SELECT` (left click), high word the icon id. The menu is the window titled `ReactorTrayFlyout`; its items have
  AutomationIds `tray-open`, `tray-stop-<name>`, `tray-restart-<name>`, `tray-more`, `tray-start-engine`,
  `tray-stop-engine`, `tray-stop-cancel`, `tray-stop-confirm`, `tray-quit`.

## The exec terminal

**Who owns what.** `ContainerDetailSession` owns the shell's stream: it creates and starts the
exec, then runs two pumps, output (engine to the attached `IExecOutput`, waiting on it each
time) and input (its own loop, so a key never waits behind output). `DetailState` holds only
the status (`ExecState`: not started, starting, running, ended with an exit code, failed), the
generation and the command (in memory only, for "Starting …" and the missing-shell hint); `DetailReducer.Exec` decides everything. A stop on purpose closes the write
side where there is one (TCP, TLS; not Docker Desktop's pipe), gives the shell 500 ms, then
disposes the stream; nothing it still sends is shown. Bytes are never decoded, logged, put in
the state or in an exception message Berth writes, and the command is never logged.

**Ending what the shell leaves.** The engine keeps a TTY shell running after its connection
closes (risk R7, measured on both engines), so each shell is created with
`BERTH_EXEC=<16 hex>` in its environment (`ExecCleanUp`). When a shell is ended on purpose
(stop, tab left, page left, connection switched or retired, app exit, which waits at most 1 s),
fails after it ran (the terminal page died, the stream broke), or its stream ends while the
engine still runs it (a detach), the reducer asks for `CleanUpExecEffect`: a detached exec in
the same container that sends HUP, and nothing else, to every process whose `/proc/<pid>/environ`
holds that exact entry. `nohup` work survives, as it would in a closed terminal. It runs only
while the container runs (not paused or restarting) on a live connection, once per shell,
under its own 3 s limit; for a shell still starting it waits until the create and start
returned and runs only when an exec was created. A failure is logged in fixed words only.

**Detach keys.** The engine watches a TTY exec's input for a detach sequence and uses
Ctrl+P Ctrl+Q when the exec names none (it cannot be turned off). Every exec is created with
`EngineClient.ExecDetachKeys` (`ctrl-^,ctrl-@` three times): only a lone Ctrl+^ is held back
until the next key.

**The message protocol** lives in Core: `Presentation/TerminalProtocol` builds and parses
every message (JSON with a string `t`; bytes as base64 in `d`; sizes 2 to 1000 columns and 1
to 500 rows; messages over 4 MB are dropped). `terminal.js` is the page's half. Messages from
the page: `ready`, `input`, `resize`, `ack`, `copy`, `paste`, `leave`; to the page: `output`,
`paste`, `reset`, `enabled`, `focus`, `theme`.

**Flow control.** `TerminalBridge` copies each chunk the pump hands it into one buffer and
posts it on the UI thread (chunks that arrive while a post is pending go together).
`TerminalFlow` counts what xterm has not acknowledged: above 1 MiB the pump waits, below
256 KiB it goes on. Input is capped at 2 MiB waiting for the shell (`ExecInput`); a piece over
the cap is dropped whole and the tab says so. A paste is one `input` message, up to 2 MB.
`TerminalScreen` decides which shell's output is shown: each write carries the detail
session it came from and the shell's generation, so an old session's or an old shell's output
never reaches the new screen.

**Keys.** With the focus in the terminal every key goes to the shell, Escape and Alt+Left
included (`ShellKeys` never acts while a WebView2 is on the focus path: `ShortcutFocus`). F6
leaves for the tab strip; Ctrl+C copies when text is selected and interrupts otherwise;
Ctrl+Shift+C and Ctrl+Insert copy; Ctrl+V and Shift+Insert paste. The page matches the letter the layout gives (`e.key`) and falls back to the key position (`e.code`) only on a non-Latin layout; AltGr (Ctrl+Alt) never copies or pastes. Browser accelerator keys (F5,
Ctrl+F, Ctrl+P, Ctrl+R) are off.

**The host component, `TerminalView`.** It builds its `WebView2()` element once per name and
WebView generation (`UseMemo`), so a page rendering ten times a second hands the control the
same element: never hand it anything new per render. Its handlers are read through the
`TerminalHandle` when called, so new delegates in the props cost nothing. The WebView serves
`Assets/Terminal` as `https://terminal.berth.example/` (`SetVirtualHostNameToFolderMapping`)
with no other origin reachable, no host objects, no script dialogs, no autofill, no built-in
error page, devtools only in Debug, and a background set before the page draws (no white
flash). It is closed, which ends its browser processes, on unmount and also when it leaves the
window while still mounted (the navigation cache keeps a left page mounted); a new WebView is
made when the page is shown again. A failed browser process is recovered by
`TerminalRecovery` (reload or recreate) and the tab shows "The terminal stopped working. Start
again." The theme goes through the handle: `IsDark` and `ThemeChanged` follow the WebView's
`ActualTheme` (Reactor's `UseIsDarkTheme()` does not follow a live switch); High Contrast
maps the colours to the system window colours.

**Updating xterm.js.** Download the two packages' tarballs from the npm registry, check their
integrity against the registry, copy `lib/xterm.js`, `css/xterm.css`, `lib/addon-fit.js` and
the licence over the files in `Assets/Terminal/` unchanged, and update the versions and
integrity in `THIRD-PARTY-NOTICES.txt` and the README. `.gitattributes` keeps them byte for
byte.

**What the spike found** (Task 1): Reactor's `WebView2()` element survives the page's
re-renders; the page is ready in about 400 ms; `winapp ui screenshot` shows the WebView's
content; UI Automation does not see the terminal's text (read it through the debug channel);
the terminal's size is also shown as text (`exec-size`).

**Checks without a keyboard (Debug only).** `BERTH_OPEN_EXEC=<container name>` opens that
container's page on the Exec tab once its row is listed; `BERTH_EXEC_COMMAND=<command>` makes
the tab choose Custom with that command. A Debug host sets `window.berthDebug`, and the page
then exposes the xterm object as `window.berthTerminal`. Start Berth with
`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`, find the page at
`http://127.0.0.1:9222/json` (its URL holds `terminal.berth.example`) and evaluate through the
DevTools protocol: `berthTerminal.input("echo hi\r", true)` types through xterm's own input
path (page, host, session, engine), and `berthTerminal.buffer.active` reads the screen. A
Release build clears every `WEBVIEW2_*` variable at start and has neither switch nor
`berthDebug` (check the trimmed `Berth.dll` for the strings).

## The Files tab

**Listing.** `DetailReducer.Files` picks the way (`ListingChoice`): in a running container (not paused, not restarting) one
non-TTY exec of `sh -c <script> sh <path>` (`ExecListing`: the path is an argument, never in the script; `find` + `stat -c
'%f/%s/%Y/%n/'` then a NUL then each link's name and `readlink` target, NUL-ended; parsed without guessing because no name holds
"/" or NUL; checked identical on alpine, ubuntu 24.04 and busybox 1.36). Every failure of it (exit 3: cannot enter; 127: tools
missing; the engine refusing the exec; output that does not parse) falls back to the archive listing under the same generation;
a refused exec or missing tools mark the container `ExecUnavailable`. The archive listing (`ArchiveListing`) reads only headers
of `GET archive`, the first level under the top entry, within 256 MiB and 15 s, and says when it stopped early; a path whose stat is a
link is read again as `path/.`; a path whose stat is not a folder is "… is not a folder." without reading it. 10,000 entries at most.

**Transfers** live in `EngineSession` (not the detail session), so they go on while another page is shown; a switch of connection
drops them and a lost connection interrupts them. `ArchiveTransfer` runs them. Paths and names are never logged: fixed words and
exception type names only.

**Download safety** (`ArchiveExtractor`; the archive is untrusted): entries must lie under the top entry (no absolute path, no
`..`, no other top name; the joined path is checked against the folder again); symbolic and hard links are skipped and counted,
never created or followed; devices, FIFOs and others are skipped and counted; names go through `WindowsNames` (forbidden and
control characters and a trailing dot or space become `_`, device names get `_` in front) and Windows-equal names keep the first;
content streams to disk and stops under 200 MB free; everything is written as `.berth-<hex>.partial` beside the target and moved at
the end, removed on failure, cancel and Quit (`EngineSession.StopTransfersAsync`, a second at most; a crash can leave one); nothing is
replaced unless the user said yes (`TransferConfirmations`, Cancel the default), and then a folder merges file by file (a junction
on the way or at the top is left alone; a merge that fails half way keeps what it moved). Selected items whose names meet on Windows
keep the first. Links on this computer (`LocalFiles.IsLink`) are reparse points with a link target only: symbolic links and junctions;
OneDrive's cloud files and deduplicated files are ordinary files.

**Upload**: `PUT archive?noOverwriteDirNonDir=true` without `copyUIDGID` (files owned by root). Before anything is sent the folder must
be there (a stat, then the archive call for the engine's own message), every local file must open (`LocalFiles.Inspect`; else Berth's
own text, never a lost connection), and each top name is looked up in the container, four at a time: one there that the user has not
agreed to replace makes the transfer wait (`TransferStatus.Asking`, `TransferNeedsAnswer`) and the panel asks; Replace sends it
again with those names, and it looks again just before sending (`UploadCheck`). Never from the listing on screen. It cannot be undone.
A timed-out call fails the transfer and keeps the connection.

**Checks write only into `berth-check*` containers and only under `/tmp/berth-check/` there**, and download only under
`%TEMP%\berth-check\` on the host; remove both afterwards. Never write into, delete from or upload to any other container. Debug
builds have a `files-debug-command` box and `files-debug-run` button on the tab: `browse <path>`, `download <path>[;…]|<local
folder>`, `upload <local path>[;…]` (into the folder shown), each through the same questions as the pickers, so a check needs
no dialog or keys. AutomationIds: `files-tab`, `files-up`, `files-refresh`, `files-path` (breadcrumb), `files-edit-path`,
`files-path-box`, `files-download`, `files-upload`, `files-table` (rows are named "Folder etc", "File hosts", "Link mtab → …"),
`files-way-note`, `files-note`, `files-waiting`, `files-error`, `files-empty`, `files-loading`, `files-transfers`,
`transfer-<id>`, `transfer-status-<id>`, `transfer-notes-<id>`, `cancel-transfer-<id>`, `show-transfer-<id>`,
`dismiss-transfer-<id>`, `transfers-clear` (Clear finished), `sort-name`, `sort-size`, `sort-modified`; a replace question is
`confirm-text` with `PrimaryButton` and `CloseButton`.

## Charts

The Stats tab's four charts (CPU, memory, disk read/write, network I/O, two by two from 760
effective pixels, one column below) are drawn by hand in `StatsChart` with Reactor's D3
primitives (`LinearScale`, `D3Grid`, `D3Axes`, `D3AreaPath`, `D3LinePath`, `D3Canvas`).
Reactor's built-in chart element takes its y domain from the data (not from 0), draws one
series and has no reference line, so it cannot show a 0-based axis, two series or a limit
line. The axes, figures and screen-reader summaries are decided in Core (`StatsCharts`). The
two series colours are the one place with colour values in code: the theme has no chart
tokens. The terminal's sixteen ANSI colours are the third (`Presentation/TerminalTheme`, with
a contrast test against the surface the terminal is drawn on: the card fill over the solid
background, #fbfbfb light and #2b2b2b dark).

## Rendering at ten renders a second

A detail page re-renders up to ten times a second while a log floods. A control handed a
new array or menu on every render leaked native memory at that rate: the header's
breadcrumb got a new item array each render and the page grew by about 20 MB a minute.
Build such things once (a static, or a cached array reused while its input is the same) or
memoize them (`UseMemo` keyed on what they show, as the page does for the header and the
tab strip; record props so an unchanged child is skipped). Before adding a control to a
page that renders often, check that it does not get a fresh collection, menu or command
list on every render, and measure the working set of a Release build over a few minutes
with `berth-check-noisy` flooding.

## Commands

```powershell
dotnet build Berth.slnx                  # must finish with 0 warnings, 0 errors
dotnet test                              # all green; integration tests are skipped
dotnet run --project src/Berth.App       # launches with package identity
```

## Integration tests

`tests/Berth.Core.Tests/Integration/` runs the client and the session against a real
engine. Its tests use `[EngineFact]` and are reported as skipped unless `BERTH_ENGINE`
holds an engine address:

```powershell
$env:BERTH_ENGINE = 'tcp://127.0.0.1:2375'; dotnet test
```

They need the docker CLI on PATH and `alpine:latest` on the engine; the CLI is run with
`DOCKER_HOST` set to the same address. Each test makes its own containers, volumes,
networks and image tags named `berth-it-<guid>` and removes them in `finally`;
`alpine:3.19` is pulled when it is missing and never removed. They wait on what they
observe (lines arriving, a busy sample, a state change) with a 60 s safety net, never on
fixed sleeps. They cover connect, list, actions, the event stream, the session, inspect,
logs (framed and TTY), stats, the detail session ending its streams when the container
stops, images (list, inspect, pull, removing a tag), volumes (create, list, remove),
networks (list, remove), running a container from a request, the session seeing a
volume and network made by the CLI, and exec (output and exit code, two-way input, the
starting size and a resize, `TERM`, a missing shell, the 409 of a stopped or paused
container, a megabyte through raw `cat`, Ctrl+P and Ctrl+P Ctrl+Q, a detail session's shell,
closing the write side, the clean-up sparing `nohup` and another shell, and what a closed
shell leaves, written to the test output), and SSH (`SshIntegrationTests` on a throwaway host they build, `SshStandIn`:
dial-stdio answering `/_ping`, the host key question with no sign-in tried, the list over SSH, a wrong password tried once, key
files with and without a passphrase, a changed key blocked before sign-in, a user docker refuses, a host without the CLI, an exec
with its write side closed, streams past a lowered `MaxSessions`, a stopped host). `[DesktopEngineFact]` tests run only against Docker
Desktop's pipe: removing unused volumes (narrowed to the test's own volumes).

Run them against the daily engine and, when it is there, against the second engine on the
named pipe (prepend Docker Desktop's `resources\bin` to PATH there, or image pulls fail on
`docker-credential-desktop`):

```powershell
$env:BERTH_ENGINE = 'tcp://127.0.0.1:2375'; dotnet test --filter "FullyQualifiedName~Integration"
$env:PATH = "$env:LOCALAPPDATA\Programs\DockerDesktop\resources\bin;$env:PATH"
$env:BERTH_ENGINE = 'npipe:////./pipe/dockerDesktopLinuxEngine'; dotnet test --filter "FullyQualifiedName~Integration"
Remove-Item Env:BERTH_ENGINE                # without it they are reported as skipped
```

Afterwards `docker ps -a`, `docker volume ls`, `docker network ls` and `docker image ls`
show nothing named `berth-it-` on either engine.

**Checks and removing.** On the daily engine (`tcp://127.0.0.1:2375`) a test or a manual
check creates and removes only things named `berth-check*` or `berth-it-*` (tag an image as
`berth-check/alpine:test` rather than removing one that was there) and never runs a prune or
"Remove unused …": the owner's unused images, volumes and networks would go. "Remove unused
…" is confirmed only on Docker Desktop's connection, and only when everything its
confirmation names is `berth-ref*` or made for that check; otherwise cancel it.

## Constraints

- Target frameworks: `net11.0` (Core, tests), `net11.0-windows10.0.26100.0` (App). Reactor `0.1.0-preview.18` exactly.
- No `.xaml` files. UI is pure C#.
- `Berth.Core` has no WinUI or Reactor reference. Pure logic lives there and is unit-tested.
- `dotnet build Berth.slnx` finishes with 0 warnings and 0 errors. Fix warnings; do not suppress them.
- No unit test may need a Docker engine. Integration tests run only when `BERTH_ENGINE` is set.
- JSON uses a source-generated `JsonSerializerContext` (Release builds are trimmed). No reflection-based serialization.
- Package identity `Koderi.Berth`, publisher `CN=Koderi`, publisher display name "Koderi", display name "Berth".
- No Docker name, logo or colours in the UI. Windows 11 styling from Reactor `Theme.*` tokens only; no hard-coded colours except the state dots, which also use theme tokens (the `Theme.SystemFillColorSuccess` family; look up the exact token in `reactor.api.txt`).
- The engine's error message is shown as-is. Never reword or swallow it.
- The log never records secrets, environment values or container log contents.
- Engine API: use the lower of the engine's `ApiVersion` and `1.56`. Refuse engines below `1.44`.

## Design: propose, do not wait to be asked

The owner should not have to spot layout and usability problems themselves. When you build
or change a screen, you own how it looks and behaves at every size, and you raise what is
wrong or could be better before they do.

- **Look at what you built.** After any UI change, run the app and take screenshots
  (`winapp ui screenshot -a Berth -o shot.png`) and read them: at a narrow window (about
  1200 wide) and a wide one (1600 or more), in dark and in light. A screen nobody looked at
  is not done. If the desktop session is disconnected the screenshots are black: say so and
  ask for it to be connected; do not report the look as checked.
- **Use the room there is, and degrade when there is none.** An action that fits on the
  row is a button on the row; when the window is too narrow it moves into an overflow menu,
  it is never dropped. The same goes for columns and text: truncate with an ellipsis and a
  tooltip, and prefer giving the room back (narrower chrome, fewer fixed widths) over
  truncating. `ContainersPage` does this with `UseBreakpoint(ContainerRowView.WideLayoutMinWidth)`.
- **Compare with the reference.** For a screen Docker Desktop also has, look at Docker
  Desktop first and match its structure: hierarchy, alignment, what sits in which column,
  how each state is drawn, what is a button and what is in a menu. Its name, logo and
  colours stay out. When Docker Desktop is installed and running on the machine (the pipe
  `\\.\pipe\dockerDesktopLinuxEngine` exists), use the real thing: start the same throwaway
  containers on its engine (`docker -H npipe:////./pipe/dockerDesktopLinuxEngine ...`, names
  starting `berth-ref`), put it in the state you are designing for (running, exited with an
  error, paused, a compose project), and read `winapp ui screenshot -a "Docker Desktop"` and
  `winapp ui inspect -a "Docker Desktop"` side by side with Berth's. Otherwise use the
  screenshots in the `docker/docs` repository listed in the spec.
- **Propose, with a recommendation.** When you see a better layout, a missing affordance
  (keyboard shortcut, context menu, sorting, empty state), an inconsistency, or a rule below
  that the current screen breaks, say so in your report as a short list: what, why, and
  what you recommend. Small and clearly right: do it and say you did. A change of behaviour
  or a matter of taste: propose it and wait.
- **Use the design skills.** They are enabled for this repository in `.claude/settings.json`:
  - `design` (Anthropic): run `design-critique` on a screenshot of every new or changed
    screen and put its priority recommendations in your report; use `ux-copy` for every
    label, empty state, error text and dialog; run `accessibility-review` before a screen is
    called done.
  - `frontend-design` (Anthropic): visual direction (hierarchy, typography, spacing) when a
    screen is new or reshaped. It is written for the web; take its judgement, not its CSS.
- **Read the platform guidance before UI work**, not after:
  - The `winui` plugin (also enabled in `.claude/settings.json`): skills
    `winui-design` (control choice, window sizing, theming, the anti-pattern table),
    `winui-ui-testing` and `winui-code-review`. Its XAML advice does not apply (no XAML
    here); its design rules do.
  - Reactor's own: `agentkit\skills\design.md` and `agentkit\skills\design-docs\`
    (`layout-and-scaling.md`, `typography-and-colors.md`, `theme-aware-resources.md`,
    `control-styles.md`, `code-review-checklist.md`) under the package folder named in the
    next section.
- **Rules that have already bitten here:**
  - Every editable text box is `Widgets.TextField` (or `Widgets.SearchBox`, built on it): uncontrolled,
    it starts from its value when it mounts and reports every change. A controlled box dropped
    the first edit twice in phase 2. A box whose value can change from outside it is mounted
    again (a new key) to show the change, as the connection editor's address is when TLS turns on.
  - Sizes, paddings and margins are multiples of 4.
  - Text that can be long sits in a `GridSize.Star()` column with `TextTrimming` and a
    tooltip; trimming never happens inside `FlexRow`, `HStack` or an `Auto` column.
  - An icon must not be mistakable for another control (the outline Stop glyph looked like
    an empty checkbox next to the row checkboxes).
  - A child row's markers line up under its parent's (one column for the chevron, one for
    the state dot), and a child's name drops what the parent row already says.
  - Destructive actions confirm, name what will be lost, and default to Cancel.
  - Light and dark working does not mean High Contrast works; that has not been checked yet (the terminal maps its colours
    to the system's under High Contrast, unchecked on screen).
  - A Reactor `FlexRow` or `FlexColumn` takes all the room it is offered: a flex row of buttons in a toolbar left the search box
    none, a flex column in a dialog made it as tall as the window, and a flex column in a grid cell kept long text from wrapping.
    Use a `Grid`, `HStack` or `VStack` (`DialogParts.Stack`) there. A negative vertical margin in a flex column crashes its
    layout ("Non-negative number required").
  - Keyboard shortcuts are registered with `ShellKeys` (a page's `PageKeys`), never with a `CommandHost`: Reactor's host takes its
    key even while its command is disabled (an inner disabled Escape kept the page's Escape from running) and works only while the
    focus is inside it (not after a click in the sidebar).

## Reactor

Docs ship in the NuGet package:
`%USERPROFILE%\.nuget\packages\microsoft.ui.reactor\0.1.0-preview.18\agentkit\`
(`SKILL.md` overview and gotchas, `reactor.api.txt` every public signature,
`skills\*.md` per topic). Grep `reactor.api.txt` for a signature instead of guessing.

## Driving the running app

Start it against an engine with `$env:DOCKER_HOST='tcp://127.0.0.1:2375'; dotnet run --project src/Berth.App`
(`DOCKER_HOST` only decides the first connection when there are no settings yet). Settings
and the log (`settings.json`, `berth.log`) are in the package's `LocalState` folder,
`%LOCALAPPDATA%\Packages\Koderi.Berth_<id>\LocalState`.

The WinApp CLI (`winget install Microsoft.winappcli --source winget`) can inspect and
drive it: `winapp ui inspect -a Berth`, `winapp ui invoke|set-value|send-keys ... -a Berth`,
`winapp ui screenshot -a Berth -o shot.png`. Run `winapp --help` or
`winapp <command> --help` before guessing at syntax. Useful selectors: row actions have
AutomationIds `stop-<name>`, `start-<name>`, `restart-<name>`, `pause-<name>` or `resume-<name>` (in a window narrower than 1300 these sit in the `more-<name>` menu) and
`delete-<name>`; the Retry button of the bar shown above a kept list while disconnected is
`retry-connection`; connections on the settings page have `edit-<name>` and `delete-<name>`;
dialogs confirm with `PrimaryButton`. The connection switcher is the `Connection` ComboBox;
after expanding it, its items are in `winapp ui inspect -a Berth --type ListItem`.

Above the list, `stats-total-cpu` and `stats-total-memory` carry the totals in their names;
`sort-cpu` sorts by CPU. A row's name is the link `open-<name>`, which opens its detail page.
The images, volumes and networks pages: `pull-image`, `create-volume`, `remove-unused-images`,
`remove-unused-volumes`, `remove-unused-networks` (below 1300 effective px the secondary ones are in
`toolbar-more`), `delete-selected`, `image-usage`, `volume-usage`, `network-summary`; per row `run-<tag>`,
`delete-<name>` (an image by its tag), `open-<volume>` (its page), `containers-<network>` (a menu of the attached
containers). Dialogs: `pull-reference`, `pull-dialog-status`; `run-name`, `run-outcome`;
`volume-name`, `volume-create-error`; a remove confirmation's text is `confirm-text`.

Engine control and the window: the disconnected view's `disconnected-title`, `start-engine`, `retry-disconnected` and
`engine-command-failure`; the bar above a kept list's `start-engine-bar`; the status bar's `engine-status` (a menu with
"Start engine" or "Stop engine…" when the connection has engine control; the stop's question is `confirm-text`); the
connection editor's `engine-control`, `engine-start-command`, `engine-stop-command`, `wsl-distro`, `wsl-default`; Settings'
`keep-running-in-tray`, `start-with-windows` and `start-with-windows-note`. The tray menu is in "The tray and the window".

On the detail page:

- Header: `back-to-list` (Esc; its name says where it goes), `detail-status`, and the same row actions (`stop-<name>`,
  `more-<name>`, `delete-<name>`, ...); below it `detail-error` (a failed read, with Retry). The navigation's `SettingsItem` and the title bar's
  `PART_BackButton` go to Settings and back to the page.
- Tabs: the SelectorBar `detail-tabs`; select one with `winapp ui invoke Stats --type
  ListItem -a Berth` (`Logs`, `Inspect`, `Bind mounts`, `Exec`, `Files`, `Stats`).
- Logs: `log-lines` (the log), `log-notes`, `log-empty`, `log-error`, `log-latest`; toolbar
  `log-search-toggle`, `log-timestamps`, `log-wrap`, `log-copy`, `log-clear`; search
  `log-search` (an Edit: `set-value`/`get-value`), `log-match-count`, `log-previous`,
  `log-next`, `log-search-close`. A `set-value` sent right after the search opens can be
  lost; read it back with `get-value`.
- Inspect: `inspect-sections`, `inspect-section-<title>`, `inspect-chip-<title>`,
  `inspect-copy-<section>-<name>`, `inspect-raw-toggle`, `inspect-raw-json`,
  `inspect-copy-json`.
- Bind mounts: `bind-mounts`, `bind-mount-open-<index>`, `bind-mounts-empty`.
- Exec (the tab is named `Exec`): toolbar `exec-shell` (the picker), `exec-custom` and
  `exec-run` with `exec-custom-error`, `exec-size` (its name says "Terminal size: N columns,
  M rows"), `exec-external`, `exec-copy-command`, `exec-external-refusal`; status
  `exec-status` (starting), `exec-ended` with `exec-start-again`, `exec-failed` with
  `exec-retry`, `exec-input-note`, `exec-webview-error`; placeholders `exec-not-running`,
  `exec-paused`, `exec-restarting`, `exec-waiting`, with `exec-start-container` and
  `exec-resume-container`. The terminal itself is a WebView named "Terminal for <name>";
  drive it through the debug channel ("The exec terminal").
- Stats: `stats-tab`, `stats-loading`, `stats-not-running`, `stats-error`, `stats-waiting`;
  the cards `stats-cpu`, `stats-memory`, `stats-disk`, `stats-network`. The figures drawn beside each
  title are hidden from UI Automation; each card's title carries the latest and highest
  figures as its name ("CPU usage 49.8%. Highest in the last 2 minutes: ...").

Checks that drive the terminal through WebView2's remote debugging port
(`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=...`) need a Debug build: a Release build clears every
`WEBVIEW2_*` variable at start.

For memory measurements use a Release build without devtools:
`dotnet build -c Release -p:PublishTrimmed=false src/Berth.App`, then
`dotnet run -c Release --no-build -p:PublishTrimmed=false --project src/Berth.App`
(`-c Release` alone fails with NETSDK1102 because trimming needs a self-contained publish).

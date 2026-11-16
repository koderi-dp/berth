# Berth

A Windows desktop GUI for a Docker engine (containers, images, volumes, networks; local
and remote engines). UI is pure C# with
[Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor)
(`0.1.0-preview.18`) on WinUI 3, .NET 11. There is no XAML: do not add `.xaml` files.

Phases 1 and 2 are built: the shell, connections (TCP, TLS, named pipe), the engine client,
the session, the Containers and Settings pages (phase 1), and a container's detail page with
Logs, Inspect, Bind mounts and Stats tabs plus live CPU and memory on the list (phase 2).
Images, volumes and networks, the exec terminal, engine control and tray, files and SSH
are not built yet.

## Layout

```
Berth.slnx                  the solution: /src/ (Berth.App, Berth.Core), /tests/
src/Berth.Core/             plain net11.0 library, no WinUI/Reactor reference; pure logic, unit-tested (root namespace Berth)
  Connections/              ConnectionProfile, EngineAddress (tcp:// and npipe:// parsing), ConnectionDraft (editor logic)
  Settings/                 AppSettings, SettingsStore (atomic save, damaged-file handling, first-run save), FirstRun (the
                            settings when there are none), SettingsFile (the tolerant read shape and its checks),
                            SettingsJson, ConnectionEdits
  Transport/                IEngineTransport, TcpTransport (plain and TLS), NamedPipeTransport, TransportFactory
  Engine/                   EngineClient (IEngineApi): version negotiation, info, list (with each container's mounts and
                            network names), actions, create, event stream, inspect, logs (StreamLogsAsync), stats
                            (StreamStatsAsync); images (list, inspect with the raw answer, remove, prune, pull as a stream of PullProgress),
                            volumes (list, create, remove, prune of every unused one), networks (list, inspect with attached
                            containers, remove, prune), disk usage (/system/df for images and volumes). Pulls, disk usage,
                            prunes and streams have no deadline; other calls 30 s (stop, restart and image and volume removes 90 s).
                            EngineHttp, EngineJson (source-generated), ApiVersion, ConnectionTester, EngineTime, JsonLines
                            (line-delimited JSON streams), LogFrames (the engine's multiplexed log framing), LogLines (frames
                            or TTY text to LogLine), AnsiText (terminal output to plain text), StatsMath (CPU and memory from
                            a raw stats line), ImageReference (a pull reference split into name and tag or digest, as the
                            docker CLI does), DiskUsageMapping (/system/df to DiskUsage: the engine's totals from API 1.52,
                            computed as the CLI does before),
                            Models/ (ContainerSummary, ContainerDetails/ContainerInspection, ContainerCreateRequest/
                            PortBinding, ImageSummary/ImageDetails/ImageDeleteResult/PullProgress, VolumeSummary,
                            NetworkSummary/NetworkDetails/NetworkEndpoint, DiskUsage, PruneResult, LogLine, StatsSample)
  Text/                     text made from engine records for the user to paste: DockerRunCommand (the `docker run`
                            command of an inspected container, PowerShell or bash quoting; ShellKind)
  State/                    value types that more than one layer holds or reads: LogBuffer (last 10,000 lines),
                            StatsHistory (last 120 samples, as chart series), ContainerSort (the list's saved sort choice),
                            ResourceList/ListStatus/RemovalBatch (the images, volumes and networks lists and their
                            batches of removes), ImagePull (a pull's layers), DiskUsageState, ContainerRun (the run
                            dialog's result), ImageInspect (the image read for the run dialog and Inspect)
  Session/                  EngineState, SessionEvents, SessionEffects, SessionReducer (all list behaviour, including list
                            stats), with ResourceEvents, ResourceEffects and SessionReducer.Resources for images, volumes,
                            networks, disk use, pulls, running a container and reading an image; EngineSession (runs the reducer and its
                            effects; reports failures to an optional error sink; opens detail sessions on its current
                            connection), its parts in EngineSession.Connection, .StatsWatch, .Resources and .Pulls (progress
                            batched to ten state changes a second per pull), Backoff;
                            DetailState, DetailEvents, DetailReducer (all detail page behaviour), ContainerDetailSession
                            (runs the detail reducer: inspect, log and stats streams of one container)
  Presentation/             ContainerRows, ComposeGrouping, ContainerFilter, ContainerSorting, ContainerStatus, BulkSelection,
                            RowActions, EngineCounts, AgeFormat, PortFormat, ByteFormat; RowSelection (checked rows of any
                            list: ISelectableRow, what a bulk action and a confirmed one send), ListSort (the images, volumes
                            and networks tables' sort); ImageRows, VolumeRows, NetworkRows (rows, filters, sort, empty texts),
                            Confirmations (what a delete or "Remove unused" sends, item by item, and its dialog text), PullFigure (a pull's progress
                            line), RunDraft (the run dialog's fields, checks and create request); detail page: LogView, LogSearch,
                            InspectSections, StatsCharts (axes, figures, screen-reader summaries), StatsSummary (list strip
                            and CPU column texts), DetailPage (the detail page's lifecycle: when it opens, closes, refreshes
                            or leaves its session, and which state its tabs show)
  Logging/, Storage/        FileLog, AtomicFile
src/Berth.App/              UI and wiring (packaged single-project MSIX app, assembly Berth, root namespace Berth)
  Program.cs                entry point: log, services, first connect, window
  AppLog.cs                 berth.log: fixed wording and exception type names only (FileLog.Describe)
  LocalData.cs              the folder for settings and the log (package LocalState, or %LOCALAPPDATA%\Berth unpackaged)
  AppServices.cs            settings, the session for the active connection (replaced on a switch) and the detail session of
                            the open container page (OpenDetail, CloseDetail, DetailChanged); sends session failures and a
                            set-aside settings file to the log
  EngineConnection.cs       an EngineClient plus the transport it runs on, disposed together
  App.cs                    the window component: title bar, navigation (AppRoute: Containers, Images, Volumes, Networks,
                            ContainerDetail, Settings), status bar; switches list stats on and off (see below) and tells the
                            session which of the images, volumes and networks lists is wanted when its page is shown
  Hooks/                    EngineStateHook.cs (EngineStateComponent, the base class for components that show the
                            engine state), DetailStateHook.cs (the open detail session's state as an external store),
                            UiThreadSubscription.cs (shared by both: follows a replaceable source and coalesces its
                            thread-pool changes into one pending UI-thread callback), SettingsHook.cs (settings as an
                            external store), ListViewHook.cs (ListPageComponent, the base class of the four list pages:
                            UseListView keeps a list's search, filter, sort and collapsed groups by route in
                            ListViewStore, so a page the navigation cache dropped starts again from them; UseListSearch)
  Components/               ConnectionSwitcher, StatusBar, DisconnectedView (no list yet), ConnectionBar (disconnected
                            above a kept list), ErrorBar, Widgets (glyphs, text styles, TextField and SearchBox, OneLine,
                            RetryBar), ContainerParts (action buttons, menus, state mark and port list shared by a list
                            row and the detail header), ListPage (a list page's sections, toolbar, search box, bulk bar
                            and its shortcuts) and ListTable (columns with wide and compact widths, header with sortable
                            titles, rows, the virtualized table, loading and empty states), shared by the four lists;
                            ShellKeys (the app's shortcuts at the window root, Ctrl+F, F5, Delete, Escape, Alt+Left,
                            acting for the page shown through the PageKeys it registers), DeleteDialog, DetailHeader
                            (breadcrumb, name, ID, image, ports, status, actions), StatsStrip (total CPU and memory
                            above the list)
  Pages/                    ContainersPage, ContainerRowView, ImagesPage, VolumesPage, NetworksPage, SettingsPage,
                            ConnectionEditor, ContainerDetailPage (header, tab strip, keeps a detail session open while
                            shown)
    Detail/                 LogsTab (follow, search, timestamps, wrap, copy, clear), InspectTab (sections and raw JSON),
                            BindMountsTab, StatsTab (four charts), StatsChart (one chart card)
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
| `Text` | `Engine` (the model records it formats) |
| `State` | `Engine` |
| `Session` | `Engine`, `State`, `Text` |
| `Presentation` | `Engine`, `State`, `Session`, `Text` |
| `Settings` | `Connections`, `State`, `Storage` |
| `Logging` | nothing |
| `Storage` | nothing |

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

## Charts

The Stats tab's four charts (CPU, memory, disk read/write, network I/O, two by two from 760
effective pixels, one column below) are drawn by hand in `StatsChart` with Reactor's D3
primitives (`LinearScale`, `D3Grid`, `D3Axes`, `D3AreaPath`, `D3LinePath`, `D3Canvas`).
Reactor's built-in chart element takes its y domain from the data (not from 0), draws one
series and has no reference line, so it cannot show a 0-based axis, two series or a limit
line. The axes, figures and screen-reader summaries are decided in Core (`StatsCharts`). The
two series colours are the one place with colour values in code: the theme has no chart
tokens.

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
`DOCKER_HOST` set to the same address. Each test makes its own container named
`berth-it-<guid>` and removes it in `finally`. They wait on what they observe (lines
arriving, a busy sample, a state change) with a 60 s safety net, never on fixed sleeps.
They cover connect, list, actions, the event stream, the session, inspect, logs (framed and
TTY), stats and the detail session ending its streams when the container stops.

Run them against the daily engine and, when it is there, against the second engine on the
named pipe (prepend Docker Desktop's `resources\bin` to PATH there, or image pulls fail on
`docker-credential-desktop`):

```powershell
$env:BERTH_ENGINE = 'tcp://127.0.0.1:2375'; dotnet test --filter "FullyQualifiedName~Integration"
$env:PATH = "$env:LOCALAPPDATA\Programs\DockerDesktop\resources\bin;$env:PATH"
$env:BERTH_ENGINE = 'npipe:////./pipe/dockerDesktopLinuxEngine'; dotnet test --filter "FullyQualifiedName~Integration"
Remove-Item Env:BERTH_ENGINE                # without it they are reported as skipped
```

Afterwards `docker ps -a --filter name=berth-it-` shows nothing on either engine.

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
  - Light and dark working does not mean High Contrast works; that has not been checked yet.
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
On the detail page:

- Header: `back-to-list` (Esc), `detail-status`, and the same row actions (`stop-<name>`,
  `more-<name>`, `delete-<name>`, ...); below it `detail-error` (a failed read, with Retry). The navigation's `SettingsItem` and the title bar's
  `PART_BackButton` go to Settings and back to the page.
- Tabs: the SelectorBar `detail-tabs`; select one with `winapp ui invoke Stats --type
  ListItem -a Berth` (`Logs`, `Inspect`, `Bind mounts`, `Stats`).
- Logs: `log-lines` (the log), `log-notes`, `log-empty`, `log-error`, `log-latest`; toolbar
  `log-search-toggle`, `log-timestamps`, `log-wrap`, `log-copy`, `log-clear`; search
  `log-search` (an Edit: `set-value`/`get-value`), `log-match-count`, `log-previous`,
  `log-next`, `log-search-close`. A `set-value` sent right after the search opens can be
  lost; read it back with `get-value`.
- Inspect: `inspect-sections`, `inspect-section-<title>`, `inspect-chip-<title>`,
  `inspect-copy-<section>-<name>`, `inspect-raw-toggle`, `inspect-raw-json`,
  `inspect-copy-json`.
- Bind mounts: `bind-mounts`, `bind-mount-open-<index>`, `bind-mounts-empty`.
- Stats: `stats-tab`, `stats-loading`, `stats-not-running`, `stats-error`, `stats-waiting`;
  the cards `stats-cpu`, `stats-memory`, `stats-disk`, `stats-network`. The figures drawn beside each
  title are hidden from UI Automation; each card's title carries the latest and highest
  figures as its name ("CPU usage 49.8%. Highest in the last 2 minutes: ...").

For memory measurements use a Release build without devtools:
`dotnet build -c Release -p:PublishTrimmed=false src/Berth.App`, then
`dotnet run -c Release --no-build -p:PublishTrimmed=false --project src/Berth.App`
(`-c Release` alone fails with NETSDK1102 because trimming needs a self-contained publish).

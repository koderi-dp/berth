# Berth

Berth is a Windows desktop GUI for a Docker engine, used as a daily driver on a machine
that runs docker-ce inside WSL. It is meant to cover containers, images, volumes and
networks, and to manage engines on other machines too. This version covers connections,
the containers list and each container's detail page with a terminal in the container, and
the images, volumes and networks pages.

The UI is pure C# with [Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor)
(`0.1.0-preview.18`, pinned) on WinUI 3. There is no XAML.

## What works in this version

- **Connections.** Engines are reached over TCP (`tcp://host:port`), TCP with TLS (a CA
  file and an optional client certificate and key) or a named pipe (`npipe:////./pipe/name`).
  The first run creates one connection from `DOCKER_HOST`, or the default named pipe. The
  Settings page adds, edits, tests and deletes connections; the title bar switches between
  them. Berth speaks the lower of the engine's API version and 1.56, and refuses engines
  below API 1.44 (Docker 25).
- **Containers.** The Containers page lists every container, running or not, with its
  state, image, status, ports (published ports open in the browser), CPU and age.
  Containers of a compose project are grouped under the project. The list can be searched,
  limited to running containers, and sorted by name, image, status, CPU or age.
- **Live CPU and memory.** While the list is shown, the running containers' total CPU (out
  of the engine's CPUs) and memory (out of its memory) sit above it, and each running
  container's CPU is in its row. They update every second and stop while another page is
  shown or the window is minimized.
- **Actions.** Start, stop, restart, pause, resume and delete per container; start, stop
  and delete for all checked rows at once. Deleting asks first and names what will be
  removed; running containers are stopped as part of the delete. A container's `docker run`
  command can be copied for PowerShell or for bash, from its row and from its page. "Open
  terminal" in a running container's row menu opens its page on the Exec tab.
- **Container detail.** A container's name opens its page: its state, ID, image, ports,
  status and actions at the top, and five tabs.
  - **Logs** follows the log as it is written (the last 1000 lines first, up to 10,000 kept),
    with timestamps and wrapping that can be turned off, search with next and previous
    match, copy all and clear. Logs of containers started with a terminal (`-t`) are read
    as well; colour codes are removed.
  - **Inspect** shows the container's configuration in sections (platform, command, state,
    image, ports, runtime, mounts, environment, labels, networks), each value copyable, or
    the engine's raw JSON.
  - **Bind mounts** lists the host paths mounted into the container with their container
    path and access; a host path on this machine (such as `/mnt/c/...` in WSL) opens in
    File Explorer.
  - **Exec** is a terminal with a shell in the container (see below).
  - **Stats** draws CPU, memory, disk read/write and network I/O over the last two minutes,
    with the CPU and memory limits when the container has them.
  When the container stops, the streams end and the page shows its new state; when it is
  removed, the page returns to the list and says so.
- **Exec: a terminal in the container.** The Exec tab of a running container starts a shell
  and shows it in a terminal (xterm.js, bundled with the app; it works with no network). The
  picker above it chooses `sh` (the default), `bash`, `ash` or a custom command; choosing
  another ends the shell and starts the new one. `sh`, `bash` and `ash` start by themselves
  whenever the tab is shown; a custom command runs only when Run (or Enter) is pressed, never
  again by itself when the tab or the page is shown again. The terminal follows the window's size, its
  colours follow the light or dark theme, and the size is shown above it. While the terminal
  has the keyboard, every key goes to the shell, Escape and Alt+Left included; F6 leaves the
  terminal for the tab strip. Ctrl+C copies when text is selected and interrupts otherwise
  (Ctrl+Shift+C and Ctrl+Insert always copy); Ctrl+V and Shift+Insert paste. Ctrl+P reaches the shell as in any terminal: Berth names its own detach keys
  (a sequence nobody types), so the engine does not hold Ctrl+P back or end the session on
  Ctrl+P Ctrl+Q as it does for `docker exec -it`. When the shell exits the tab says with what
  code and offers "Start again"; a shell that is not in the image says so and offers `sh`. A
  stopped, paused or restarting container shows why there is no terminal, with Start or
  Resume.
  - **What happens to the shell.** Every shell Berth starts carries the environment variable
    `BERTH_EXEC` (a random id), which everything started from it inherits. The engine keeps a
    terminal's shell running after its connection closes, so when Berth is done with a shell
    (the tab is left, the shell is changed, the connection is switched, Berth is closed, the
    terminal stopped working, or the session ended while the container still ran it) it runs
    a short clean-up command in the same container that sends a hang-up (HUP) to every
    process carrying that id: the shell and everything started from it end. That includes a
    service or server started by hand from the shell (`nginx`, a dev server) even when it went
    into the background, which closing a terminal window would not end; start long-running
    work with `nohup`, or with "Open in external terminal", to keep it. The docker CLI does not
    do this: after `docker exec -it`, a shell whose connection dropped keeps running. A program
    that handles the hang-up itself is not ended either. In an image without `sh`, `grep` or
    `/proc`, or where the container's user cannot read a process, nothing is ended, and Berth
    is not told: only a clean-up that the engine refused to start is logged.
  - **Open in external terminal** runs the same shell with `docker exec -it` in a Windows
    Terminal tab, or, when Windows Terminal is not installed, in a console window of its own.
    It needs the docker CLI on `PATH` (otherwise the button is disabled and says why). The
    command names the engine with `-H` (and its TLS files), so it always reaches the connection's
    engine. Berth also removes `DOCKER_HOST`, `DOCKER_CONTEXT`, `DOCKER_TLS_VERIFY`,
    `DOCKER_CERT_PATH` and `DOCKER_TLS` from what it starts; the console's docker never sees
    them, but Windows Terminal may give its tab its own environment, so a `DOCKER_TLS_VERIFY` or
    `DOCKER_CERT_PATH` set for your user can still reach docker there. Windows Terminal re-reads what it is given, so a custom command is refused there
    (and the button says "Copy it instead") when an argument is empty, is `--`, holds a double
    quote, a `%`, a tab or line break or half of a surrogate pair, or has a space and ends in a
    backslash; with Windows Terminal installed such a command is not handed to the console
    either. The console refuses only a NUL or half a surrogate pair. A connection with a client
    certificate but no key (or the other way round) is refused.
  - **Copy command** copies that `docker exec -it` command, quoted for PowerShell 7.
- **Images.** The Images page lists a row per tag (and one per untagged image) with its
  tag, ID, age, size and whether a container uses it, above a line with the engine's image
  disk use. It can be searched, filtered (all, in use, unused, dangling) and sorted.
  "Pull image" pulls a reference and shows each pull's layers and bytes in the list while it
  runs, with Cancel. An image's Run opens a dialog for the container's name, host ports for
  the ports the image exposes, volumes or bind mounts and environment variables; the new
  container's page opens once it runs, and a refusal is shown in the dialog as the engine
  gave it. Inspect shows an image's raw JSON.
- **Volumes.** The Volumes page lists each volume with whether a container uses it, its age
  and size, above the volume disk use. "Create" makes a named volume. A volume's name opens
  its page: driver, mount point, size, age and the containers that mount it (each a link to
  its page).
- **Networks.** The Networks page lists each network with its driver, scope, age and the
  containers attached to it (a menu of links to them). The built-in networks cannot be
  deleted.
- **Removing images, volumes and networks.** Delete (per row, for all checked rows, or with
  the Delete key) and "Remove unused …" ask first. "Remove unused …" names every item it will
  remove, and says so when the search or filter hides some of them; a delete of checked rows
  names the first ten and counts the rest (the rows checked are on screen). Both say the space
  freed when the engine tells it, a delete names the containers that use an image or volume
  (the engine refuses those) and the containers that lose a network. Nothing is forced: what
  the engine refuses stays, and the bar above the list says what was removed, refused or
  skipped. "Remove unused …" waits until the container list and its own list have been read
  on this connection, since they tell what is in use; until then its confirmation says why
  it cannot remove anything.
- **Live updates.** The lists follow the engine's event stream, so a container, image,
  volume or network made from the command line appears within a second without a refresh.
- **Connection state.** The title bar shows whether Berth is connecting, connected (with
  the engine version) or disconnected; the status bar shows the engine's container counts.
  When the engine cannot be reached and no list has been loaded, the Containers page shows
  why, as the engine or the network reported it, with a Retry button. When the connection
  drops while a list is on screen, the list stays, a warning bar above it gives the same
  reason (or "Connecting…" during an attempt) with Retry, and the list's checkboxes and
  actions are disabled until the engine is back; search and "running only" still work.
  Berth reconnects on its own (after 1 s, doubling up to 30 s), or at once with Retry.
  Until a connection's first list arrives, the page shows a progress ring.
- **Settings file.** `settings.json` may leave out optional fields (TLS, pipe name, port,
  list options, active connection) and may hold fields Berth does not know. A file Berth
  cannot use is kept as `settings.bad.json`, Berth starts with the first-run connection,
  and a warning says so until it is closed. The first-run settings are saved at once.
- **Log.** `berth.log` records start and exit, connection switches, a settings file set
  aside, and what failed: connecting, loading the list, the event stream, a container
  action, a container page's inspect, log or stats stream, a shell (starting it, its stream,
  resizing it, reading its exit code, ending what it left in the container), the terminal
  page, starting an external terminal, reading a container for its run command, a list stats
  stream, a copy to the clipboard, saving the settings, and unexpected errors. Nothing typed
  into or printed by a terminal, and no command, is ever logged. A failure is logged by its exception type only, never its message, so
  the log holds no addresses, engine messages, secrets, environment values, container log
  lines or inspect data. Settings, "Open log folder" opens its folder.

## Not built yet

In the order they are planned:

1. Starting and stopping a local engine, a tray icon, a single instance, start with Windows.
2. Files: browse, download and upload in a running container.
3. SSH connections: password and key sign-in, host key prompt, Credential Manager.

For a later polish pass:

- Choosing and reordering the list's columns.
- Charts above the containers list.

## Third-party software

The terminal is [xterm.js](https://github.com/xtermjs/xterm.js) (`@xterm/xterm` 6.0.0) with
its fit addon (`@xterm/addon-fit` 0.11.0), MIT licensed, copied into
`src/Berth.App/Assets/Terminal/` and shipped in the package. Its licence is
`src/Berth.App/Assets/Terminal/LICENSE-xterm.txt`; `THIRD-PARTY-NOTICES.txt` lists it.

## Requirements

- Windows 11, x64 or ARM64.
- .NET 11 SDK.
- Developer Mode turned on, to run the packaged app from source (`dotnet run` registers a debug package identity).
- A reachable Docker engine, API 1.44 or newer (for example docker-ce in WSL listening on `tcp://127.0.0.1:2375`).
- The Microsoft Edge WebView2 Runtime for the terminal (part of Windows 11).
- The docker CLI on `PATH`, only for "Open in external terminal".

## Build, test, run

From the repository root:

```powershell
dotnet build Berth.slnx                  # must finish with 0 warnings, 0 errors
dotnet test                              # all green; no unit test needs a Docker engine
dotnet run --project src/Berth.App       # launches with package identity
```

Integration tests run against a real engine when `BERTH_ENGINE` is set, and need the
docker CLI on PATH and the `alpine:latest` image on the engine (`alpine:3.19` is pulled
when missing and kept). They create and remove only their own containers, volumes,
networks and image tags named `berth-it-<guid>`, and never prune. The test of "Remove
unused volumes" runs only against Docker Desktop's pipe. Without the variable they are
reported as skipped.

```powershell
$env:BERTH_ENGINE = 'tcp://127.0.0.1:2375'; dotnet test --filter "FullyQualifiedName~Integration"
$env:BERTH_ENGINE = 'npipe:////./pipe/dockerDesktopLinuxEngine'; dotnet test --filter "FullyQualifiedName~Integration"
```

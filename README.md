# Berth

Berth is a Windows desktop GUI for a Docker engine, used as a daily driver on a machine
that runs docker-ce inside WSL. It covers containers, images, volumes and networks, on this
machine and on others: connections (TCP, TLS, a named pipe, SSH), the containers list and
each container's detail page with a terminal in the container, and the images, volumes and
networks pages, starting and stopping a local engine, and a tray icon that keeps Berth
running when its window is closed.

The UI is pure C# with [Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor)
(`0.1.0-preview.18`, pinned) on WinUI 3. There is no XAML.

## What works in this version

- **Connections.** Engines are reached over TCP (`tcp://host:port`), TCP with TLS (a CA
  file and an optional client certificate and key), a named pipe (`npipe:////./pipe/name`)
  or SSH (`ssh://user@host:port`, below).
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
    as well. Colour codes are shown as colours (the sixteen terminal colours, 256 colours
    and 24-bit colour, bold, dim, italic and underline), with a toolbar toggle to show the
    log plain; other terminal codes are removed.
  - **Inspect** shows the container's configuration in sections (platform, command, state,
    image, ports, runtime, mounts, environment, labels, networks), each value copyable, or
    the engine's raw JSON.
  - **Bind mounts** lists the host paths mounted into the container with their container
    path and access; a host path on this machine (such as `/mnt/c/...` in WSL) opens in
    File Explorer.
  - **Exec** is a terminal with a shell in the container (see below).
  - **Files** browses the container's files, downloads and uploads (see below).
  - **Stats** draws CPU, memory, disk read/write and network I/O over the last two minutes,
    with the CPU and memory limits when the container has them.
  When the container stops, the streams end and the page shows its new state; when it is
  removed, the page returns to the list and says so.
- **Files: browse, download, upload.** The Files tab lists a folder of the container: name (a
  folder, file or link glyph; a link shows its target), size, modified and mode, folders first;
  Name, Size and Modified sort it (folders stay first).
  The path above it is a breadcrumb; the pencil turns it into a box to type a path (Enter goes
  there, Escape leaves it). Up, Refresh (F5), double click or Enter on a folder, Backspace or
  Alt+Up for the folder above, Ctrl+C copies the selected rows' paths; the right-click menu has
  Open, Download… and Copy path. In a running container the folder is listed by a short command
  run in it (`sh` with `find`, `stat` and `readlink`, present in busybox and GNU images); in a
  stopped container, or one without those tools, from the engine's archive of the folder, which
  the tab says ("Listing through the archive"). A folder over 10,000 entries shows the first
  10,000 and says so; a large folder listed through the archive may stop early and says so too.
  The engine's message is shown as it is for a path that is missing or not a folder.
  - **Download…** saves the selected files and folders (a folder with everything under it) into
    a folder you choose. What comes from the container is not trusted: nothing is written outside
    that folder (a path with `..`, an absolute path or another top folder is skipped); symbolic
    and hard links are never created or followed, only skipped and counted; devices, FIFOs and
    sockets are skipped and counted; a name Windows cannot hold is saved under a safe name (each
    of `< > : " \ | ? *` and control characters becomes `_`, a trailing dot or space becomes `_`,
    and `CON`, `NUL`, `COM1` and the other device names get `_` in front) and counted; two names
    that are the same to Windows keep the first. Each item is written under a temporary name and
    renamed at the end, so a cancelled or failed download, or Quit during one, leaves nothing
    partial; only a crash can leave a `.berth-….partial` item, which is safe to delete. A download
    stops when less than 200 MB would be free on the drive. Nothing already in the folder is
    replaced without asking (Cancel is the default); a folder that is already there keeps its other
    files (a merge that fails half way keeps the files it moved). The summary says what was saved
    and what was skipped and why; "Show in folder" opens it in Explorer.
  - **Upload…** puts files, or a folder with everything under it, into the folder shown; files
    dragged from Explorer onto the table do the same. Before it sends anything it checks that every
    file can be read and asks the container whether something of the same name is there; if so it
    asks before replacing it. An upload cannot be undone: a cancelled one leaves in the container
    what the engine already unpacked. Symbolic links and junctions inside an uploaded folder are
    skipped (OneDrive's cloud files are uploaded as ordinary files).
  - Transfers show under the table (about three rows, then a scroll; "Clear finished" removes the
    ended ones) with their progress and Cancel; several can run at once, and
    they go on while you look at other pages. A switch of connection ends them. Berth does not
    delete, rename or edit files in a container.
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
  the Delete key) and "Remove unused …" ask first. "Remove unused …" and a delete of several
  checked rows show a table of what will go: a row per image (its tags on one row, an untagged
  image as "Untagged" with its ID), volume or network, with its ID or driver, age or scope and,
  for images and volumes, the space it frees ("Shared" when its layers stay with other images).
  Every row starts checked; untick what to keep (Space toggles a row, Ctrl+A checks them all).
  Under the table a line counts what is checked and the space it frees, and the Remove button
  says how many it removes. "Remove unused …" says so when the search or filter hides some of
  the items; a delete names the containers that use an image or volume (the engine refuses
  those) and the containers that lose a network. Only the checked rows are sent, planned again
  when Remove is clicked, so anything that came into use meanwhile stays. Nothing is forced: what
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
- **Engine control.** A connection can have engine control: a start command and a stop
  command, each one command line typed as in a console window (no shell: nothing is expanded,
  redirected or chained). The connection editor's "Engine control" section has the two
  commands and "Use the WSL default" with a picker of the WSL distributions:
  `wsl.exe -d <distro> -u root -- systemctl start docker` and
  `wsl.exe -d <distro> -u root -- systemctl stop docker.socket docker` (docker-ce in WSL is
  socket-activated, so the stop also stops its socket, or the next client in the distro would
  start it again). While the engine is not reachable the page offers "Start engine"; the status
  bar's engine item has a menu with Start engine or Stop engine, and a stop asks first, naming
  how many running containers will stop. A command runs at most 60 seconds (then it and what it
  started are ended); a failure shows what failed and the command's last lines as it wrote
  them. After a start Berth reconnects at once and every second for half a minute; after a
  stop the page says "The engine was stopped." and Berth does not reconnect until Start engine
  or Retry. The log gets the exit code only, never the command or its output.
- **Tray.** Berth has an icon in the notification area: the app icon with a state dot (green
  running, amber working, red not reachable, grey stopped) and a tooltip with the connection
  and its state. Left click opens the window; right click opens the menu: Open Berth, the
  connection and its state, the running containers (by name, at most 15) each with Stop and
  Restart, Start engine or Stop engine when the connection has engine control (a stop asks
  first, in the menu), and Quit Berth. A container action from the menu that fails shows in
  the error bar and as a notification.
- **Closing the window** hides it to the tray while "Keep running in the tray when the window
  is closed" (Settings, on by default) is on; the first time, a notification says so. Quit in
  the tray menu exits: a shell open in the Exec tab is ended as on any exit, and the icon is
  removed. While hidden, the pages do no rendering work and list stats are not followed; the
  connection stays up so the tray stays right.
- **One Berth at a time.** Starting Berth again brings the running one's window forward
  (back from the tray) instead of opening a second.
- **Start with Windows** (Settings) uses the package's startup task; started that way, Berth
  starts hidden in the tray. The switch shows the real state, and says so when it was turned
  off in Windows settings or by a policy.
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

## SSH connections

An SSH connection (`ssh://user@host:port`, port 22 when left out) reaches the engine of
another machine through its docker CLI, as `docker -H ssh://…` does: Berth opens one SSH
session and runs `docker system dial-stdio` on it for each stream (the list, the events,
logs, stats, a terminal, a transfer), speaking to the engine over the command's input and
output. Everything else in Berth works the same over it.

- **What the remote needs.** An SSH server that allows the user to sign in by password or by
  key, the docker CLI on the user's `PATH`, and the user allowed to use the engine (in the
  `docker` group, or a rootless engine of their own). Berth runs exactly
  `docker system dial-stdio`, nothing typed by the user. A host without the docker CLI, or a
  user the engine refuses, shows the remote's own message.
- **Signing in.** By password (the default) or by a private key file, with its passphrase if
  it has one. The password and the passphrase are kept in Windows Credential Manager, as
  generic credentials named `Koderi.Berth/<connection id>/password` and
  `…/passphrase` for this user on this computer; never in `settings.json` or the log.
  Without one kept, Berth asks when it connects ("Remember" is on by default; turned off, the
  secret is used for that one attempt). A refused password is never tried again on its own:
  Berth stops and asks, because a few refusals can lock an account. Deleting a connection
  deletes its secrets.
- **Host keys.** The first connection to a host shows its key's type and SHA-256 fingerprint
  (as `ssh-keygen -lf` prints it) and asks; nothing but the key exchange reaches the host
  before you accept (not your user name, not your password). Accepted keys are saved in
  `settings.json` with the connection, per host and port. A host that offers a different key
  later is blocked with a warning naming both keys, and the new key cannot be accepted there:
  if you know why it changed (the host was reinstalled), open Settings, edit the connection,
  remove the saved key, then connect and check the new one.
- **Many streams.** sshd allows 10 channels per session by default (`MaxSessions`). Berth
  puts up to 10 streams on one session and opens another session (signed in the same way)
  when the host refuses a channel or the session is full, up to 8 sessions; a second session
  closes when its last stream does.
- **Keep-alives.** Each session sends one every 15 seconds, so a host that went away is
  noticed and Berth reconnects with the accepted key and the kept secret, without asking (a
  secret entered without "Remember" is asked for again).
- **Engine control is off for SSH connections:** its commands would run on this computer,
  not on the remote host.
- **External terminal.** "Copy command" and "Open in external terminal" use
  `docker -H ssh://user@host:port exec -it …`: the docker CLI then makes its own connection
  with your ssh, its agent, keys and `known_hosts`, and may ask for the password itself.

## Not built yet

For a later polish pass:

- Choosing and reordering the list's columns.
- Charts above the containers list.

## Third-party software

SSH connections use [SSH.NET](https://github.com/sshnet/SSH.NET) 2026.0.1 (MIT), with
BouncyCastle.Cryptography 2.7.0 (MIT) and Microsoft.Extensions.Logging.Abstractions (MIT),
listed in `THIRD-PARTY-NOTICES.txt`.

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
when missing and kept). The SSH tests build a throwaway SSH host image from `alpine:3.20`
(named `berth-it-ssh-<guid>`, with openssh-server and the docker CLI) and run it with the
engine's socket mounted and its port on 127.0.0.1 only; they remove it afterwards. They create and remove only their own containers, volumes,
networks and image tags named `berth-it-<guid>`, and never prune. The test of "Remove
unused volumes" runs only against Docker Desktop's pipe. Without the variable they are
reported as skipped.

```powershell
$env:BERTH_ENGINE = 'tcp://127.0.0.1:2375'; dotnet test --filter "FullyQualifiedName~Integration"
$env:BERTH_ENGINE = 'npipe:////./pipe/dockerDesktopLinuxEngine'; dotnet test --filter "FullyQualifiedName~Integration"
```

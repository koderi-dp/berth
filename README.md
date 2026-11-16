# Berth

Berth is a Windows desktop GUI for a Docker engine, used as a daily driver on a machine
that runs docker-ce inside WSL. It is meant to cover containers, images, volumes and
networks, and to manage engines on other machines too. This version covers connections,
the containers list and each container's detail page.

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
  command can be copied for PowerShell or for bash, from its row and from its page.
- **Container detail.** A container's name opens its page: its state, ID, image, ports,
  status and actions at the top, and four tabs.
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
  - **Stats** draws CPU, memory, disk read/write and network I/O over the last two minutes,
    with the CPU and memory limits when the container has them.
  When the container stops, the streams end and the page shows its new state; when it is
  removed, the page returns to the list and says so.
- **Live updates.** The list follows the engine's event stream, so a container started from
  the command line appears within a second without a refresh.
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
  action, a container page's inspect, log or stats stream, saving the settings, and
  unexpected errors. A failure is logged by its exception type only, never its message, so
  the log holds no addresses, engine messages, secrets, environment values, container log
  lines or inspect data. Settings, "Open log folder" opens its folder.

## Not built yet

In the order they are planned:

1. Images (pull, run, remove, prune), volumes and networks.
2. A terminal in a running container.
3. Starting and stopping a local engine, a tray icon, a single instance, start with Windows.
4. Files: browse, download and upload in a running container.
5. SSH connections: password and key sign-in, host key prompt, Credential Manager.

## Requirements

- Windows 11, x64 or ARM64.
- .NET 11 SDK.
- Developer Mode turned on, to run the packaged app from source (`dotnet run` registers a debug package identity).
- A reachable Docker engine, API 1.44 or newer (for example docker-ce in WSL listening on `tcp://127.0.0.1:2375`).

## Build, test, run

From the repository root:

```powershell
dotnet build Berth.slnx                  # must finish with 0 warnings, 0 errors
dotnet test                              # all green; no unit test needs a Docker engine
dotnet run --project src/Berth.App       # launches with package identity
```

Integration tests run against a real engine when `BERTH_ENGINE` is set, and need the
docker CLI on PATH and the `alpine:latest` image on the engine. They create and remove
their own containers named `berth-it-<guid>`. Without the variable they are reported as
skipped.

```powershell
$env:BERTH_ENGINE = 'tcp://127.0.0.1:2375'; dotnet test --filter "FullyQualifiedName~Integration"
$env:BERTH_ENGINE = 'npipe:////./pipe/docker_engine'; dotnet test --filter "FullyQualifiedName~Integration"
```

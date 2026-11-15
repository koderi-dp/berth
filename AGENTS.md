# Berth

A Windows desktop GUI for a Docker engine (containers, images, volumes, networks; local
and remote engines). UI is pure C# with
[Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor)
(`0.1.0-preview.18`) on WinUI 3, .NET 11. There is no XAML: do not add `.xaml` files.

Phase 1 is built: the shell, connections (TCP, TLS, named pipe), the engine client, the
session and the Containers and Settings pages. Later phases (SSH, container detail, images,
volumes and networks, exec terminal, engine control and tray, files) are not.

## Layout

```
Berth.slnx                  the solution: /src/ (Berth.App, Berth.Core), /tests/
src/Berth.Core/             plain net11.0 library, no WinUI/Reactor reference; pure logic, unit-tested (root namespace Berth)
  Connections/              ConnectionProfile, EngineAddress (tcp:// and npipe:// parsing), FirstRun, ConnectionDraft (editor logic)
  Settings/                 AppSettings, SettingsStore (atomic save, damaged-file handling), SettingsJson, ConnectionEdits
  Transport/                IEngineTransport, TcpTransport (plain and TLS), NamedPipeTransport, TransportFactory
  Engine/                   EngineClient (IEngineApi): version negotiation, info, list, actions, event stream;
                            EngineHttp, EngineJson (source-generated), ApiVersion, ConnectionTester, Models/
  Session/                  EngineState, SessionEvents, SessionEffects, SessionReducer (all behaviour),
                            EngineSession (runs the reducer and its effects), Backoff
  Presentation/             ContainerRows, ComposeGrouping, ContainerFilter, BulkSelection, AgeFormat, PortFormat
  Logging/, Storage/        FileLog, AtomicFile
src/Berth.App/              UI and wiring (packaged single-project MSIX app, assembly Berth, root namespace Berth)
  Program.cs                entry point: log, services, first connect, window
  AppServices.cs            settings and the session for the active connection; replaces the session on a switch
  EngineConnection.cs       an EngineClient plus the transport it runs on, disposed together
  App.cs                    the window component: title bar, navigation, status bar
  Hooks/                    EngineStateHook.cs (EngineStateComponent, the base class for components that show the
                            engine state), SettingsHook.cs (settings as an external store)
  Components/               ConnectionSwitcher, StatusBar, DisconnectedView, ErrorBar, Widgets
  Pages/                    ContainersPage, ContainerRowView, SettingsPage, ConnectionEditor
  Package.appxmanifest      package identity Koderi.Berth, publisher CN=Koderi
tests/Berth.Core.Tests/     xunit tests for Core; Integration/ holds the tests that need a real engine
```

Pure logic goes in Core and is unit-tested; the app project holds UI, interop and wiring only.
Where the original plan put logic in the app, it now lives in Core: the connection editor's
rules are `ConnectionDraft`, the settings page's list changes are `ConnectionEdits`, and the
containers page's checked-row logic is `BulkSelection`. The engine state reaches components
through the `EngineStateComponent` base class, not a hook function (Reactor keeps a class
component's hooks protected).

## The reducer rule

All session behaviour is in `SessionReducer.Reduce(state, event) -> (state, effects)`.
`EngineSession` only runs it: it feeds events through the reducer on one loop and carries
out the effects. New list behaviour is a new event or effect plus a reducer test in
`tests/Berth.Core.Tests/Session/SessionReducerTests.cs`, never state kept in a component.
Components read `EngineState` and dispatch events; they hold only view state (search text,
checked rows, an open dialog).

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

They need the docker CLI on PATH; it is run with `DOCKER_HOST` set to the same address.
Each test makes its own container named `berth-it-<guid>` (from `alpine:latest`) and
removes it in `finally`. Afterwards `docker ps -a` shows no `berth-it-` container.

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
AutomationIds `stop-<name>`, `start-<name>`, `more-<name>` (Restart, Pause, Resume) and
`delete-<name>`; connections on the settings page have `edit-<name>` and `delete-<name>`;
dialogs confirm with `PrimaryButton`. The connection switcher is the `Connection` ComboBox;
after expanding it, its items are in `winapp ui inspect -a Berth --type ListItem`.

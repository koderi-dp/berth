# Berth

A Windows desktop GUI for a Docker engine (containers, images, volumes, networks; local
and remote engines). UI is pure C# with
[Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor)
(`0.1.0-preview.18`) on WinUI 3, .NET 11. There is no XAML: do not add `.xaml` files.

## Layout

```
Berth.slnx             the solution: /src/ (Berth.App, Berth.Core), /tests/
src/Berth.App/         UI and wiring (packaged single-project MSIX app, assembly Berth, root namespace Berth)
  Program.cs           entry point: log, services, first connect, window
  App.cs               the window component (title bar, navigation, status bar)
  Package.appxmanifest package identity Koderi.Berth, publisher CN=Koderi
src/Berth.Core/        plain net11.0 library, no WinUI/Reactor reference; pure logic, unit-tested (root namespace Berth)
tests/                 xunit tests for Core (no test may need a Docker engine)
```

Pure logic goes in Core and is unit-tested; the app project holds UI, interop and wiring only.

## Commands

```powershell
dotnet build Berth.slnx                  # must finish with 0 warnings, 0 errors
dotnet test                              # all green
dotnet run --project src/Berth.App       # launches with package identity
```

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

The WinApp CLI (`winget install Microsoft.winappcli --source winget`) can inspect and
drive it: `winapp ui inspect -a Berth`, `winapp ui invoke|set-value|send-keys ... -a Berth`,
`winapp ui screenshot -a Berth -o shot.png`. Run `winapp --help` or
`winapp <command> --help` before guessing at syntax.

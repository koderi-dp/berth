# Berth

Berth is a Windows desktop GUI for a Docker engine, used as a daily driver on a machine
that runs docker-ce inside WSL and has no Docker Desktop. It covers what Docker Desktop's
dashboard does for containers, images, volumes and networks, and it can also manage
engines on other machines.

The UI is pure C# with [Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor)
(`0.1.0-preview.18`, pinned) on WinUI 3. There is no XAML.

## Requirements

- Windows 11, x64 or ARM64.
- .NET 11 SDK.
- Developer Mode turned on, to run the packaged app from source (`dotnet run` registers a debug package identity).
- A reachable Docker engine (for example docker-ce in WSL listening on `tcp://127.0.0.1:2375`).

## Build, test, run

From the repository root:

```powershell
dotnet build Berth.slnx                  # must finish with 0 warnings, 0 errors
dotnet test                              # all green; no test needs a Docker engine
dotnet run --project src/Berth.App       # launches with package identity
```

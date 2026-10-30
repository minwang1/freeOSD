# Monitor Control Repository Guide

## Project direction

The active implementation is **C# + Avalonia UI** for Windows and Linux. The user has directed that `python/` not be used and plans to delete it. **Do not read any files in `python/`, or run, modify, inspect, reference, or depend on its contents.** `UI-DESIGN.md` is the UI behavior and page hierarchy reference. `plan.md` is the migration roadmap.

## Architecture boundaries

- `src/MonitorControl.App/`: Avalonia presentation, navigation, view models, dialogs, and app composition. Do not call OS APIs from views or view models.
- `src/MonitorControl.Core/`: OS-independent domain models, use cases, validation, preset logic, VCP orchestration, and backend contracts. Do not reference Avalonia or platform-specific assemblies here.
- `src/MonitorControl.Platform.Windows/`: Windows monitor discovery/DDC/CI, display configuration (refresh rate/HDR), foreground process monitoring, startup and tray integrations as needed.
- `src/MonitorControl.Platform.Linux/`: Linux DDC/CI access and capability-gated desktop integrations. Keep display-server/desktop-specific behavior behind explicit interfaces and report unsupported capabilities.
- `tests/`: hardware-independent tests for Core and adapter logic. Hardware validation is documented separately and must not be represented as automated coverage.
- `python/`: excluded legacy code, planned for user deletion. Never read or otherwise access this directory for implementation, research, or verification.

Use dependency direction `App -> Core <- Platform adapters`; App composes the selected platform adapters at startup. Shared data contracts must not expose native handles, `ctypes`, P/Invoke structures, Avalonia types, or Linux command output.

## Platform and safety requirements

- Target the .NET SDK installed in the environment; the current workspace has .NET SDK 10.0.403. Pin framework/package versions deliberately and avoid floating versions in committed project files.
- Keep OS-specific code in its platform project and guard platform-only APIs at compile/runtime boundaries.
- Treat monitor writes as immediate hardware changes. Serialize DDC/CI operations per monitor and keep UI responsive.
- Refresh-rate changes must retain a timed rollback path. VCP mapping tests must restore the original value and verify restoration before reporting success.
- Platform features such as HDR, refresh rate, application focus tracking, login startup, and tray support must be capability-gated. Never claim Linux parity until implemented and validated on a named desktop/session configuration.
- Preserve existing JSON profile/automation/identified-control data compatibility where practical; document any schema migration before changing formats.
- Do not put user settings, monitor dumps, credentials, or machine-specific data into source control.

## Code conventions

- Use nullable reference types, explicit access modifiers, async APIs for I/O, and cancellation tokens for long-running work where cancellation is safe.
- Use descriptive C# `PascalCase` type/member names and `camelCase` locals/parameters. Keep UI strings resource-ready and avoid embedding platform assumptions in shared copy.
- Keep public contracts small and typed. Represent optional platform capabilities explicitly instead of using scattered OS-name checks.
- Add comments for non-obvious hardware/API behavior, not line-by-line narration.

## Work and verification

- Work from the repository root. Keep all active C# implementation independent of `python/`.
- For each implementation task, state the files changed, user-visible behavior, platform limitations, and verification performed or not performed.
- Do not claim builds, tests, physical-monitor checks, Linux checks, or package-size measurements unless actually run. Do not run tests unless requested by the user.
- Keep hardware-independent tests isolated from physical monitor requirements. Use mocks/fakes for backend contracts and restoration flows.
- Update `README.md`, `UI-DESIGN.md`, and `plan.md` when user-visible behavior, supported platforms, or migration scope changes.
- Prefer small, reviewable work packages. Avoid editing files assigned to another concurrent task; coordinate shared solution/project changes before touching them.
- Use incremental builds and reuse the project's default `bin/` and `obj/` output directories. Do not set a new `OutputPath`, create per-build artifact folders, or clean build outputs unless the user asks.

## Commands (once the solution exists)

The exact project paths may evolve as the scaffold lands. Expected command shapes are:

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
dotnet build .\MonitorControl.sln --no-restore -m:1 -v:minimal
dotnet run --project .\tests\MonitorControl.Core.Tests\MonitorControl.Core.Tests.csproj
dotnet run --project .\src\MonitorControl.App\MonitorControl.App.csproj
```

Use `dotnet test` only when the user requests tests or verification. Linux builds and runtime checks should be performed on Linux CI or a Linux host; a Windows build is not evidence of Linux compatibility.

# Accel — Build & Test Environment

This document describes file/folder organization, build/test/publish mechanics, and environment quirks for the Accel project. For architecture and design, see `CLAUDE_ARCHITECTURE.md` and `CLAUDE_DESIGN.md`. For functional overview, see `README.md`.

## Prerequisites

- **.NET 8 SDK, pinned to 8.0.424** (see `global.json`)
  - Avoids broken workload-manifest resolver in newer 8.0.x versions
  - Restore with `dotnet workload restore` if needed (implicit during build)
- **Windows 10 1803+ or Windows 11** (required for built-in `curl.exe`)
- Visual Studio 2022 optional (full IDE support; command-line only via `dotnet` CLI)

## Repository Layout

| Folder | Purpose |
|--------|---------|
| `App/` | WPF/Windows Forms UI layer (MainWindow, tabs, terminal panel, dialogs, XAML) |
| `Cli/` | Command-line argument parsing and internal verb routing (ArgParser, CliTests, etc.) |
| `Metrics/` | Event/telemetry capture and aggregation for the status-line and HTTP API |
| `Orchestration/` | Process lifecycle management, pseudoconsole (ConPTY), PTY registry, shutdown coordination, smoke tests |
| `Server/` | Kestrel-based HTTP event server, route handlers (`/events/*`, `/roots`, `/roots/tree`), WebSocket PTY routes |
| `Settings/` | Claude Code settings.json merge/diff/hook registration logic |
| `Versioning/` | Version gate and compatibility checks |
| `tests/accel.Tests/` | xUnit test project (977+ tests) covering all layers |
| `installer/` | Inno Setup script (`accel.iss`) that packages the published exe into `Accel-Setup-<version>.exe`; invoked by `publish.ps1`, never run standalone |
| `bin/`, `obj/`, `publish/`, `dist/` | Build/publish artifacts (gitignored) — `dist/` holds `publish.ps1`'s zip and installer output |
| `.serena/` | Serena code-nav cache (gitignored) |
| `.gitignore` | Standard: `bin/`, `obj/`, `.vs/`, `TestResults/`, `coverage/`, OS files |

### Root Files

| File | Purpose |
|------|---------|
| `accel.sln` | Solution file (Visual Studio 2022) with 2 projects: `accel` and `accel.Tests` |
| `accel.csproj` | Main project (SDK: Web, targets net8.0-windows, console exe with WPF/WinForms) |
| `global.json` | Locks .NET SDK to 8.0.424 with `rollForward: latestFeature` |
| `Program.cs` | Entry point; routes CLI args to ArgParser, smoke tests, and RunCombinedAsync |
| `folder.json` | JSON array of root folder paths for monitoring (format: `["C:/projects", ...]`) |
| `publish.ps1` | Wraps `dotnet publish` for the single-file executable, then packages a portable zip and (if Inno Setup is installed) a `Setup.exe` into `dist\` |

## Build Instructions

### Development Build (Debug)

```bash
dotnet build accel.sln
```

Output: `bin/Debug/net8.0-windows/accel.exe` + dependencies in separate files

**Time**: ~5–10 seconds (first build) or ~1 second (incremental)

### Release Build

```bash
dotnet build accel.sln -c Release
```

Output: `bin/Release/net8.0-windows/accel.exe` + dependencies

### Notes

- ImplicitUsings enabled; `System.IO`, `System.Net.Http` explicitly restored (see csproj comment — Web SDK + WPF overlay would drop them)
- InvariantGlobalization disabled (WPF keyboard-layout culture resolution fails under invariant mode on non-English keyboards)
- GenerateMvcApplicationPartsAssemblyAttributes disabled (avoids SDK bug with XAML compilation and Accessibility.dll Ref mismatch)
- Icon: `App/accel.ico` baked into exe
- test\accel.Tests\* explicitly excluded from main project glob (nested folder would interfere)

## Test Instructions

### Running All Tests

```bash
dotnet test accel.sln
```

Runs all 977+ xUnit tests across unit, integration, and targeted E2E categories; total runtime ~20–30 seconds.

**Verbose output (shows individual test names)**:
```bash
dotnet test accel.sln -v detailed
```

### Test Project Structure

**Location**: `tests/accel.Tests/`

**Framework**: xUnit 2.8.1 with Microsoft.NET.Test.Sdk 17.12.0

**Naming convention**: `*Tests.cs` for unit/integration tests; `*SmokeTest.cs` for E2E validation tests (see section below)

**Test count**: ~61 files, 977+ test cases covering:
- CLI parsing (CliTests, CommonCliFlagsTests)
- Settings merge/diff (SettingsMergerTests)
- Hook registration (StatusLineCommandTests, SubagentStatusLineCommandTests)
- Session/folder tree enumeration (RootsTreeRouteTests, RootsPanelViewModelTests, MonitorTreeBuilderTests)
- PTY lifecycle (PtySessionTests, PtyRegistryTests, PtyOrphanReconcilerTests)
- ConPTY interop marshalling (ConPtyTests)
- Dialog/ViewModel logic (CreateSessionDialogViewModelTests, TabsViewModelTests, McpSkillsPanelViewModelTests)
- EventServer/HTTP routes (EventServerTests, RootsRouteTests, StateQueryRoutesTests)
- Metrics/model lookup tables (EffortBarLevelTests, ModelEffortTableTests, ModelWindowTableTests)

### Smoke Tests (E2E Validation)

Smoke tests live alongside source code (not in tests/) and validate real OS resources (processes, pseudoconsoles, WebView2) that unit tests cannot:

| File | Verb | What It Tests |
|------|------|---------------|
| `Orchestration/ConPtySmokeTest.cs` | `pty-smoke-test [iter]` | ConPTY creation/teardown, handle leaks; default 50 iterations |
| `Orchestration/PtySessionSmokeTest.cs` | `pty-session-smoke-test [cycles]` | Real Job Object lifecycle + child process teardown; default 10 cycles |
| `Orchestration/PtyRegistryStressTest.cs` | `pty-registry-stress-test [tabs]` | Concurrent PTY registry under load (N real children, force-kill, double-closes); default 30 tabs |
| `Orchestration/PtyShutdownReconcileSmokeTest.cs` | `pty-shutdown-orphan-test` | App shutdown grace paths (Dispose, console control, ProcessExit) + orphan reconciliation |
| `App/TerminalE2ESmokeTest.cs` | `terminal-e2e-smoke-test` | WebView2 terminal panel wired to PtySession over /pty/{tabId} WebSocket on real Kestrel server |
| `App/TabsE2ESmokeTest.cs` | `tabs-e2e-smoke-test` | Real XAML tab strip, ISessionSelectionService, panel D reattach, real child teardown |

**How to run** (manually, not via `dotnet test`):

```bash
# Run ConPTY smoke test with 50 iterations
accel.exe pty-smoke-test 50

# Run PTY registry stress test with 30 tabs
accel.exe pty-registry-stress-test 30

# Run app shutdown orphan reconciliation test
accel.exe pty-shutdown-orphan-test

# Run terminal WebView2 E2E test
accel.exe terminal-e2e-smoke-test

# Run tab strip XAML/binding E2E test
accel.exe tabs-e2e-smoke-test
```

All smoke tests print diagnostics to stdout and exit with 0 on success, non-zero on failure.

### Running Tests via Visual Studio

Open `accel.sln` in Visual Studio → Test Explorer → Run All Tests (or right-click a test file / test class to run selectively).

## Publish / Release Instructions

### Publish as Single-File Executable

```bash
dotnet publish accel.csproj -r win-x64 -c Release
```

**Output**: `bin/Release/net8.0-windows/win-x64/publish/accel.exe` (~179 MB)

**Properties**:
- Self-contained (includes .NET 8 runtime, no external runtime needed on target machine)
- Single file (PublishSingleFile=true, IncludeNativeLibrariesForSelfExtract=true)
- Native libraries bundled (WebView2Loader.dll, xterm.js assets, application icon)
- Requires no .NET installation on end-user machine

### Using publish.ps1

```powershell
.\publish.ps1
```

Runs the dotnet publish command above, then packages the redistributables into `dist\` (version read
from `accel.csproj`'s `<Version>`, the single source of truth also read by `App/Controls/AppVersionInfo.cs`):
- Confirms publish succeeded (checks exit code) and that `accel.exe` exists at the expected path
- Always produces a portable zip (`dist\Accel-<version>-win-x64.zip`): a staged copy of `accel.exe` +
  the `wwwroot\xterm` terminal assets + the default `folder.json` fallback (`.pdb`/xml docs/`global.json`/
  `web.config` are excluded — none are needed at runtime)
- If Inno Setup 6's `ISCC.exe` is found on PATH or its default install location, also compiles
  `installer\accel.iss` into `dist\Accel-Setup-<version>.exe`; otherwise skips that step with a warning
  (the zip is still a complete redistributable) — install Inno Setup from
  https://jrsoftware.org/isinfo.php to enable it
- Reports final file size in MB

### Artifact Details

- **Size**: ~179 MB (single executable includes .NET 8 runtime + all dependencies)
- **Format**: PE32+ (x64 Windows executable)
- **Dependencies**: None on target system (the installer's `[Setup]` section notes `accel.exe` is
  already self-contained/self-extracting, so there's no .NET-runtime prerequisite page)
- **Sign/notarize**: Not automated (neither `publish.ps1` nor `installer\accel.iss` sign the output;
  add a `signtool` step with a `/d` cert path and `/sha256` if needed)

## Environment Quirks & Notes

### Implicit Usings Conflict

Accel targets both `Microsoft.NET.Sdk.Web` (for Kestrel EventServer) and `UseWPF=true`. The Web SDK includes `System.IO` and `System.Net.Http` in implicit usings, but WPF's overlay **replaces** (not merges) the implicit list, dropping those namespaces.

**Fix**: Explicitly restored in `.csproj`:
```xml
<ItemGroup>
  <Using Include="System.IO" />
  <Using Include="System.Net.Http" />
</ItemGroup>
```

Affected files: `Metrics/`, `Server/` (EventServer, RawPayloadCapture, RootsTreeBuilder, etc.)

### No InvariantGlobalization

Originally attempted to use `<InvariantGlobalization>true</InvariantGlobalization>` to reduce single-file size, but **removed** because WPF's TextBox input pipeline resolves keyboard layout culture (e.g., LCID 1036 for French keyboard) on every keystroke, throwing `CultureNotFoundException` under invariant mode.

Impact: Create-session dialog Display Name field would crash on non-English keyboards.

Solution: Accept the runtime overhead; no workaround at per-control level.

### Disabled MVC Application Parts Discovery

The `<GenerateMvcApplicationPartsAssemblyAttributes>false</GenerateMvcApplicationPartsAssemblyAttributes>` property disables an MSBuild target that scans for ASP.NET Core controllers. Accel uses Kestrel directly (manual route wiring via `MapPost`, `MapGet`, `MapWebSocketRoute`), not MVC, and the discovery target triggers a long-standing SDK bug when markup-compilation runs on a temporary wpftmp project: it fails to resolve the Accessibility.dll reference-pack assembly metadata.

### WebView2Loader.dll in Single-File

WebView2 NuGet package contributes WebView2Loader.dll (native, not managed). With `IncludeNativeLibrariesForSelfExtract=true`, the loader is bundled into the single-file payload and self-extracts at runtime; no loose file duplication needed.

### xterm.js / Terminal Panel Serving

xterm.js (WebView2 front-end, panel D) is vendored offline in `App/Controls/wwwroot/xterm/`. Served to WebView2 via `CoreWebView2.SetVirtualHostNameToFolderMapping` (not embedded as EmbeddedResource). Explicit MSBuild Content items ensure files are copied to output on both dev build and publish.

### Folder Config Search Order

Accel looks for `folder.json` (JSON array of absolute paths) in this order:

1. `%USERPROFILE%\.claude\accel-folders.json` (preferred, colocated with Accel state)
2. Executable directory / `folder.json` (portable deployment)
3. Current working directory / `folder.json` (dev/testing)

If not found or malformed, treated as empty array `[]`.

### Event Server Port

Default: **40010** (localhost/127.0.0.1)

Overridable via `--port <n>` CLI flag for multiple Accel instances or port conflicts.

### Hook Registration Best-Effort

On startup, Accel registers itself into `%USERPROFILE%\.claude\settings.json` for four hooks (SessionStart, SessionEnd, SubagentStart, SubagentStop) and two status-line commands. If registration fails (permissions, malformed JSON), a diagnostic is printed to stderr but startup continues — the app does not abort.

### Project File Organization

`accel.csproj` lives at repo root (not in `src/` subdirectory). Compiled project glob `**/*.cs` would collide with `tests/accel.Tests/`, so the csproj explicitly excludes `tests/**` from Compile, EmbeddedResource, Page, and ApplicationDefinition item groups.

### Console Entrypoint

P/Invoke ConPTY and Job Object interop declarations are marked `internal` but visible to tests via `InternalsVisibleTo` attribute so marshalling can be unit-tested. No public API exposure needed.

# Accel — Claude Code Session Activity Monitor

## What is Accel?

Native Windows C# tool that monitors Claude Code local session activity. Running `accel` with no arguments does everything in one combined process: it auto-installs itself into Claude Code hooks (`%USERPROFILE%\.claude\settings.json`) so Claude Code events are forwarded to Accel via curl POST calls, starts a local, non-HTTPS HTTP server (default port **40010**, overridable via `--port`) to receive them in-process, and opens a WinForms monitor window showing the configured root folders → Claude Code sessions → running sub-agents, refreshing live as events arrive (no polling).

## Building

### Requirements

- .NET 8 SDK (pinned to 8.0.424 via `global.json` — this avoids a broken workload-manifest resolver in newer versions)
- Windows 10 1803+ or Windows 11 (for built-in `curl.exe`)

### Build

```bash
dotnet build accel.sln
```

This builds the main `accel` project and the `accel.Tests` project (xUnit, 288 tests).

## Publishing

Publish as a self-contained, single-file Windows x64 executable:

```bash
dotnet publish accel.csproj -r win-x64 -c Release
```

**Output:** `bin/Release/net8.0-windows/win-x64/publish/accel.exe` (~179 MB)

Alternatively, run the included `publish.ps1`:

```powershell
.\publish.ps1
```

The exe requires no .NET runtime on the target machine — it includes everything needed to run.

## CLI Surface

- **`accel`** (default, no arguments) — Installs hooks into settings.json (best-effort — a refusal is printed but never aborts startup), starts the event server in-process, and opens the WinForms monitor window, all in one process. Close the window to shut everything down cleanly.
- **`accel --port <n>`** — Same as above, but bind the server (and register hooks) on a different port than the default 40010.
- **`accel --uninstall`** — Remove all Accel-registered hooks from settings.json and restore any pre-existing `statusLine`/`subagentStatusLine` settings, then exit immediately (no server/UI started).
- **`accel statusline --port <n>`** — **Internal verb, invoked by Claude Code itself as a short-lived child process.** Reads the statusline payload from stdin, posts it to the server, and re-prints the chained original status line. Always exits 0.
- **`accel subagent-statusline --port <n>`** — **Internal verb, invoked by Claude Code itself as a short-lived child process.** Reads the subagent status array from stdin and posts it to the server without printing.

No other verbs are recognized (there is no separate `run`/`install`/`ui`/`status`/`sessions` anymore).

## How It Works

1. **Hook Registration:** On startup, Accel reads `%USERPROFILE%\.claude\settings.json` and registers itself into four event hooks (`SessionStart`, `SessionEnd`, `SubagentStart`, `SubagentStop`) and two status-line commands (`statusLine`, `subagentStatusLine`).

2. **Event Forwarding:** Claude Code fires these hooks at the appropriate times, and each hook issues a curl POST request to Accel's HTTP server with the event payload as JSON.

3. **Status Line:** Accel installs itself as the `statusLine` command — when Claude Code requests a status-line update, Accel receives the payload on stdin, posts it to the server for metrics collection, and then re-invokes any pre-existing status-line command (or a default fallback) so the status bar continues to render normally.

4. **Server:** The embedded HTTP server binds to `127.0.0.1:40010` (by default), receives POST requests to `/events/*` routes, parses the JSON event payloads, prints them to the terminal, and maintains an in-memory snapshot of active sessions and subagents.

5. **Querying:** The server's HTTP GET routes (below) can still be queried externally for scripting or monitoring purposes.

6. **Root Folders Configuration:** Accel looks for a `folder.json` file (a JSON array of absolute folder paths) in this order:
   - `%USERPROFILE%\.claude\accel-folders.json` (preferred location, colocated with other Accel state)
   - `<directory of the running executable>\folder.json` (for portable deployments)
   - `<current working directory>\folder.json` (for development)
   
   Example `folder.json`:
   ```json
   ["C:/projects"]
   ```
   If no config file is found or it is malformed, Accel treats it as an empty array.

7. **UI Window:** Running `accel` opens a Windows Forms monitor window in the same process as the server, displaying the configured root folders, all Claude Code sessions under those folders (both active and historical), and for active sessions, the currently running sub-agents. It refreshes on genuine push signals (a hook/statusline POST arriving, or a change detected on disk) rather than a polling timer, debounced by ~250ms so a burst of activity collapses into a single refresh.

8. **API Endpoints:** The server exposes two additional HTTP GET routes (used by the UI and queryable externally):
   - `GET /roots` — Returns the configured root folder paths as a JSON array.
   - `GET /roots/tree` — Returns the full hierarchical tree of roots, sessions, and running sub-agents with metadata (model, effort, context usage, etc.).

## Example Usage

```bash
# Install hooks, start the server, and open the monitor window - all in one process
accel

# Same, but on a different port
accel --port 41000

# Remove all Accel hooks and exit
accel --uninstall
```

## Testing

```bash
dotnet test Accel.sln
```

Runs 271 unit tests covering settings merge/diff, hook registration, state management, CLI parsing, and session/folder tree enumeration.

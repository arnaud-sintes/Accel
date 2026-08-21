# Accel — Another Claude Code Ecosystem Layer

![image-20260818135945612](README.assets/image-20260818135945612.png)

## What is Accel?

Native Windows C# tool that monitors Claude Code local session activity. Running `accel` with no arguments does everything in one combined process: it auto-installs itself into Claude Code hooks (`%USERPROFILE%\.claude\settings.json`) so Claude Code events are forwarded to Accel by self-invoking `accel.exe notify` (no `curl.exe` involved), starts a local, non-HTTPS HTTP server (default port **40010**, overridable via `--port`) to receive them in-process, and opens a WPF monitor window showing the configured root folders → Claude Code sessions → running sub-agents, plus a file tree, `git status`, MCP-tool/Skill usage, a PTY terminal with tabs, an in-window file editor/diff viewer, and an agent graph — refreshing live as events arrive (no polling).

## Building

### Requirements

- .NET 8 SDK (pinned to 8.0.424 via `global.json` — this avoids a broken workload-manifest resolver in newer versions)
- Windows 10 1809+ or Windows 11 (for the ConPTY pseudoconsole API used to host PTY sessions) plus the WebView2 Evergreen runtime (usually preinstalled; needed by the terminal panel — `accel doctor` checks for it)

### Build

```bash
dotnet build accel.sln
```

This builds the main `accel` project and the `accel.Tests` project (xUnit, 1289 tests).

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

- **`accel`** (default, no arguments) — Installs hooks into settings.json (best-effort — a refusal is printed but never aborts startup), starts the event server in-process, and opens the WPF monitor window, all in one process. Close the window to shut everything down cleanly.
- **`accel --port <n>`** — Same as above, but bind the server (and register hooks) on a different port than the default 40010.
- **`accel --verbose`** — Same as the default start, but also prints the diagnostic console output that a normal launch suppresses (per-event lifecycle lines, the full hook-install summary).
- **`accel --uninstall`** — Remove all Accel-registered hooks from settings.json and restore any pre-existing `statusLine`/`subagentStatusLine` settings, then exit immediately (no server/UI started).
- **`accel doctor`** — Short-lived, UI-less pre-flight diagnostic. Checks that `claude` resolves to a native executable (not an npm-style `.cmd`/`.bat`/`.ps1` shim) and that the WebView2 Evergreen runtime is installed (needed by the terminal panel). Prints `[OK]`/`[FAIL]` per check and exits 1 if anything failed.
- **`accel notify --port <n> --route <path> -H "X-Accel-Hook: <Event>"`** — **Internal verb, invoked by Claude Code itself as a short-lived child process** (this is what the four installed hooks actually run instead of `curl.exe`). Reads the hook's JSON payload from stdin, POSTs it to `http://127.0.0.1:<port><path>`, and always exits 0 having printed nothing — a connection refusal (Accel not running yet) is swallowed silently rather than surfacing as a hook error.
- **`accel statusline --port <n>`** — **Internal verb, invoked by Claude Code itself as a short-lived child process.** Reads the statusline payload from stdin, posts it to the server, and re-prints the chained original status line. Always exits 0.
- **`accel subagent-statusline --port <n>`** — **Internal verb, invoked by Claude Code itself as a short-lived child process.** Reads the subagent status array from stdin and posts it to the server without printing.

No other user-facing verbs are recognized (there is no separate `run`/`install`/`ui`/`status`/`sessions` anymore). A handful of raw-string-matched dev-only smoke/stress-test verbs also exist (`pty-smoke-test`, `pty-session-smoke-test`, `pty-registry-stress-test`, `pty-shutdown-orphan-test`, `terminal-e2e-smoke-test`, `tabs-e2e-smoke-test`) — not part of the documented surface, see `CLAUDE_ARCHITECTURE.md`.

## How It Works

1. **Hook Registration:** On startup, Accel reads `%USERPROFILE%\.claude\settings.json` and registers itself into four event hooks (`SessionStart`, `SessionEnd`, `SubagentStart`, `SubagentStop`) and two status-line commands (`statusLine`, `subagentStatusLine`).

2. **Event Forwarding:** Claude Code fires these hooks at the appropriate times, and each hook runs `accel.exe notify --port <n> --route <path> -H "X-Accel-Hook: <Event>"` — Accel notifying itself rather than shelling out to `curl.exe` — which POSTs the event payload as JSON to Accel's HTTP server and always exits 0, even if Accel isn't running yet.

3. **Status Line:** Accel installs itself as the `statusLine` command — when Claude Code requests a status-line update, Accel receives the payload on stdin, posts it to the server for metrics collection, and then re-invokes any pre-existing status-line command (or a default fallback) so the status bar continues to render normally.

4. **Server:** The embedded HTTP server binds to `127.0.0.1:40010` (by default), receives POST requests to `/events/*` routes, parses the JSON event payloads, prints them to the terminal, and maintains an in-memory snapshot of active sessions and subagents.

5. **Querying:** The server's HTTP GET routes (below) can still be queried externally for scripting or monitoring purposes.

6. **Root Folders Configuration:** Accel keeps a single root-folders config file at `%USERPROFILE%\.claude\accel-folders.json` (colocated with other Accel state). It is created on demand the first time you add a root folder — no manual setup, and no elevation, even when Accel itself is installed under `C:\Program Files`. **All writes always go there.**

   For backward compatibility, *reads* still fall back to two legacy, read-only locations when that file doesn't exist yet (first one that *exists on disk* wins — even if it then fails to parse, later candidates are not tried):
   - `%USERPROFILE%\.claude\accel-folders.json` (the durable, per-user config — the only write target)
   - `<directory of the running executable>\folder.json` (legacy/portable, read-only)
   - `<current working directory>\folder.json` (legacy/dev, read-only)

   If a legacy `folder.json` is found while the durable file is still missing, its contents are migrated into `accel-folders.json` the first time a write path is resolved, so reads and writes never split across two files.

   Two on-disk shapes are supported:
   - **v1 (legacy)** — a flat JSON array of absolute folder paths:
     ```json
     ["C:/projects"]
     ```
   - **v2** — a JSON object that also carries a sparse per-session override map, keyed by session id:
     ```json
     {
       "version": 2,
       "roots": ["C:/projects"],
       "sessions": {
         "<session-id>": { "displayName": "My session", "pinned": true, "hidden": false, "lastOpenedUtc": "2026-01-01T00:00:00Z" }
       }
     }
     ```
     Adding/removing a root folder from the UI ("Add root folder…" / "Stop monitoring this folder…") reads and rewrites this same file (always upgrading it to v2 on save, via `RootFoldersConfig.Save`) — it is **not** a separate settings file. The `sessions` map holds per-session overrides. Panel A's *Rename…* writes `displayName` through it today; `pinned`/`hidden`/`lastOpenedUtc` are part of the persisted shape but not yet driven by any UI action, so they stay at their defaults in practice. This file is unrelated to Claude Code's own `%USERPROFILE%\.claude\settings.json` (the hooks file `InstallCommand`/`SettingsMerger` manage) — the two must not be confused or merged.

   If no config file is found or it is malformed, Accel treats it as an empty config (no roots, no session overrides).

7. **UI Window:** Running `accel` opens a WPF monitor window in the same process as the server, displaying the configured root folders, all Claude Code sessions under those folders (both active and historical), and for active sessions, the currently running sub-agents — see [UI Panels](#ui-panels) below for a tour of each panel. It refreshes on genuine push signals (a hook/statusline POST arriving, or a change detected on disk) rather than a polling timer, debounced by ~250ms so a burst of activity collapses into a single refresh.

8. **API Endpoints:** The server exposes two additional HTTP GET routes (used by the UI and queryable externally):
   - `GET /roots` — Returns the configured root folder paths as a JSON array.
   - `GET /roots/tree` — Returns the full hierarchical tree of roots, sessions, and running sub-agents with metadata (model, effort, context usage, etc.).

## UI Panels

The monitor window is split into five panels (`A`–`E`), each bound to its own view model — see `CLAUDE_ARCHITECTURE.md` §2.7 for the implementation details.

```
+------------------+---------------------------------------+------------------+
| Panel A (top 3/4)| Panel C (tab strip)                   | Panel B          |
| Roots / sessions | Panel D (terminal, WebView2+xterm.js) | Files (top)      |
| / sub-agents     |                                       | Git status (bot) |
| (tree, left)     +---------------------------------------+                  |
| Panel A (bottom  | Panel E (agent graph, bottom)         |                  |
| 1/4): MCP/Skills |                                       |                  |
+------------------+---------------------------------------+------------------+
```

- **Panel A — Roots / Sessions / Sub-agents** (`RootsPanelViewModel`, left column, top 3/4): a tree of the configured root folders (sorted alphabetically), every Claude Code session found under them (live or historical), and, for a live session, its currently running sub-agents. Right-clicking a row opens a context menu whose items are gated by the row's kind:
  - **On a root folder:** *Create session…* (opens the "Create session" dialog with that folder pre-filled as the working directory), *Open terminal here…* (launches a plain `cmd.exe` PTY tab rooted at that folder — no dialog, since unlike a `claude` session there is nothing to configure), *Reveal in File Explorer*, and *Stop monitoring this folder…* (dereferences the root from `accel-folders.json` — see [Root Folders Configuration](#how-it-works) above; it never deletes the folder or its contents).
  - **On a session:** *Rename…* (persisted as that session's `displayName` in the config's `sessions` override map, so it survives a restart), *Resume*, *Resume as fork…*, *Edit launch args…*, *Remove…* (only enabled for a session that's currently open in a tab).

  The panel also has a filter box at the top — typing narrows the tree to matching rows (auto-expanding to reveal a match under a collapsed row, and collapsing back when you clear it) — plus *Refresh* and *Collapse all*.

- **Panel A — MCP / Skills usage** (`McpSkillsPanelViewModel`, left column, bottom 1/4): two flat, most-used-first lists of the focused session's MCP-tool and Skill hit counts. Accel only counts `PostToolUse` hits observed while it was running, so a historical (not-currently-open) session always shows empty lists here.

- **Panel B — Files / Git** (right column, top/bottom split): a file tree for whichever folder is currently focused (top, with per-file-type icons and its own filter box), and a flat `git status` list for that same folder grouped into "Merge Conflicts"/"Staged Changes"/"Changes" (bottom, VS Code Source Control style), with stage/unstage/stage-all/discard, commit, push/pull and a branch switcher. Double-clicking a file in the tree (or an Added/Untracked/Deleted git entry) opens it in a Panel C/D tab — editable right in the window, see Panel D below; double-clicking a Modified git entry opens a side-by-side diff.

  The tree is **not** read-only: its context menu offers *New File…* and *New Folder…* (also available on the tree's blank background, targeting the focused root), *Rename / Move…* (a path-editing dialog with a folder picker, so a rename and a move are the same operation), *Delete* (to the Windows Recycle Bin) and *Delete Permanently*. Both deletes confirm first, with distinct wording and icon for the irreversible one. Any tab holding an affected file is reconciled after the operation rather than left pointing at a stale path.

  **Merge conflicts** get their own group at the top of the list, plus a banner showing which operation the repo is stopped in the middle of (merge/rebase/cherry-pick/revert) with Continue and Abort buttons. Double-clicking a conflicted row opens the same side-by-side view with the incoming side on the left and the marker-bearing working-tree file, editable, on the right — conflict regions highlighted — so a conflict is resolved by editing and saving in place. Per-row context menu: Accept ours, Accept theirs, or Mark resolved (which warns if the file still contains conflict markers).

- **"Create session…" dialog**: opened from Panel A's root context menu. Lets you set a display name, model, effort, permission mode, working directory, and (advanced/unvalidated) extra CLI arguments before spawning the PTY session. Effort is a 5-tier scale (low/medium/high/xhigh/max) gated per model family — Haiku has no reasoning-effort knob at all, so the control hides/disables itself rather than offering a setting the CLI would reject.

- **Panel C — Tab Strip** (`TabsViewModel`, top of the center column) and **Panel D — Terminal** (`TerminalView`, below the tab strip): one tab per open PTY session; double-clicking a tab renames it; selecting a tab focuses it across the whole window (Panel A highlights the matching session, Panel D reattaches its terminal — a single shared WebView2/xterm.js instance — to it over a `ws://…/pty/{tabId}` connection, Panel E rebuilds around it). Tabs also host plain shell sessions (from a root's *Open terminal here…*) and file/diff viewers, not just `claude` sessions.

- **Panel D — File editor** (shares Panel D with the terminal): a file or git-change tab opened from Panel B shows the file's content with syntax highlighting and line numbers — and, when the file exists on disk and reads as text, it is **editable**: type directly, undo/redo, then save. Saves preserve the file's original encoding, BOM, and line-ending style (LF/CRLF/mixed, trailing newline) — only your text changes, never the file's byte shape. Unsaved changes are marked with a `●` and a bold tab title, plus Save/Discard buttons in the tab header; closing a dirty tab (or quitting with dirty tabs open) prompts before anything is lost. If another writer (e.g. a running Claude Code session) changes the file on disk while you have it open, a clean tab silently reloads and a dirty one asks whether to keep your version, reload, or cancel. Deleted git entries, Modified-entry diffs, and non-text files stay read-only; markdown tabs also offer a read-only rendered-HTML preview toggle.

  **Find in document (`Ctrl+F`)**: a find bar floats over the top-right of the pane, highlighting every hit in the document (the one you are currently on in a stronger colour) with a `3/17` position counter, wrapping next/previous stepping, and match-case / whole-word toggles. It works in the single-pane file viewer and, in a side-by-side diff, on the **"After" side** — the side you are reading and, on an unstaged Modified entry, editing. Editable and read-only tabs both support it.

  Keyboard shortcuts (when a file tab is selected):
  | Shortcut | Action |
  |---|---|
  | `Ctrl+S` | Save the current file tab |
  | `Ctrl+Z` | Undo |
  | `Ctrl+Y` / `Ctrl+Shift+Z` | Redo |
  | `Ctrl+F` | Open the find bar (again to return to the query box) |
  | `Enter` / `F3` | Next match |
  | `Shift+Enter` / `Shift+F3` | Previous match |
  | `Alt+C` / `Alt+W` | Toggle match case / whole word |
  | `Esc` | Close the find bar |

- **Panel E — Agent Graph** (`AgentGraphViewModel`, bottom of the center column): a left-to-right node graph of the focused session's currently running sub-agents (parent first, bezier connectors), each card showing model badge and an `EffortBarsControl` radial gauge for its effort level (five tiers: low/medium/high/xhigh/max). It renders an explicit empty state ("no session focused" / "no longer in the tree") rather than a blank pane when there is nothing to draw.

## Example Usage

```bash
# Install hooks, start the server, and open the monitor window - all in one process
accel

# Same, but on a different port
accel --port 41000

# Remove all Accel hooks and exit
accel --uninstall

# Run pre-flight diagnostics (claude resolution, WebView2 runtime)
accel doctor
```

## Testing

```bash
dotnet test Accel.sln
```

Runs 1289 unit tests covering settings merge/diff, hook registration, state management, CLI parsing, session/folder tree enumeration, the file/git/MCP-Skills panels, git actions and merge-conflict handling, filesystem create/rename/move/delete planning, document search, file-edit buffers and encoding/line-ending round-trips, and per-model effort gating.

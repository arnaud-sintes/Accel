# Glaude UI Refactoring — Spec &amp; Audit

Status: draft. Author: Claude (orchestrator pass), pending Opus + Fable technical addenda.

## 0. TL;DR — scope reality check

Requested refactor assumes Glaude is a WPF app that launches/manages `claude` sessions.
Current codebase does neither:

- **UI stack today: WinForms**, not WPF. Single hand-built `Form` (`src/Glaude/Cli/MonitorForm.cs`), no XAML, no MVVM, zero UI NuGet packages.
- **Glaude never spawns `claude`.** It is a passive hook/monitor tool: it registers itself into the user's real `%USERPROFILE%\.claude\settings.json` hooks/statusLine, receives HTTP POSTs from Claude Code's own hook mechanism, and separately tail-reads Claude Code's transcript `.jsonl` files. There is no `Process.Start`, no PTY, no ConPTY, no terminal emulation anywhere in the repo.
- **No rename/resume/remove/stop logic exists** for sessions. Zero hits for "resume" in the codebase. "remove"/"rename" hits are all about settings.json hook bookkeeping, unrelated to sessions.
- **No persisted session list.** Sessions are discovered on demand by enumerating Claude Code's own files under `%USERPROFILE%\.claude\projects\<slug>\`. There's no Glaude-owned database/registry of sessions.
- Root folders ARE already a concept (`folder.json` / `%USERPROFILE%\.claude\glaude-folders.json`, a flat JSON string array), but with no add/remove UI, no per-root metadata.

**Conclusion:** this is not a "refactor the UI" task — it's "build a new session-orchestration + interactive-terminal-hosting product on top of Glaude's existing telemetry backend." The existing backend (`SessionState`, `MetricsPipeline`, `RootsTreeBuilder`, `RootFoldersConfig`, the event server) is genuinely reusable for populating panels A and E. Everything about launching, hosting, and interacting with a live `claude` process (panel D) and mutating sessions (create/rename/remove/resume/stop) is new.

## 1. Current-state audit (verified against repo)

### 1.1 Source tree (`src/Glaude`, excluding bin/obj)

```
Program.cs                          — entry point (top-level statements)
Cli/
  ArgParser.cs                      — verb parsing (Start, StatusLine, SubagentStatusLine, Unknown)
  DebounceCoalescer.cs              — pure debounce logic for monitor refresh
  FileBackedStatusLineChainStore.cs — persists captured statusLine/subagentStatusLine originals
  GlaudePaths.cs                    — %USERPROFILE%\.claude\settings.json path/probe helpers
  InstallCommand.cs                 — hook install/repair CLI wrapper over SettingsMerger
  MonitorForm.cs                    — WinForms monitor window (626 lines)
  MonitorTreeBuilder.cs             — pure tree/column-layout builder feeding MonitorForm (514 lines)
  ShellCommandRunner.cs             — runs/chains shell/exec-form commands (statusline passthrough)
  StatusLineCommand.cs              — `glaude statusline` internal verb
  SubagentStatusLineCommand.cs      — `glaude subagent-statusline` internal verb
  UninstallCommand.cs               — `glaude --uninstall`
Metrics/
  MetaJsonReader.cs                 — reads subagent .meta.json sidecar files
  MetricsPipeline.cs                — wires hook/statusline payloads into SessionState
  ModelWindowTable.cs               — model-id → context-window-size lookup (placeholder table)
  RootsTreeBuilder.cs               — builds root→session→agent DTO tree for UI (587 lines)
  SessionState.cs                   — in-memory session/agent state store, not persisted
  TranscriptHeadReader.cs           — bounded head-read (cwd, first-message label)
  TranscriptReader.cs                — bounded tail-read (last assistant entry, usage)
Server/
  EventPrinter.cs                   — console printer for incoming hook events
  EventServer.cs                    — ASP.NET Core minimal-API host (Kestrel, loopback only)
  RawPayloadCapture.cs              — --dump-raw diagnostic capture
  RootFoldersConfig.cs              — probes/parses folder.json / glaude-folders.json
  RootsRoutes.cs                    — GET /roots
  RootsTreeRoute.cs                 — GET /roots/tree
  StateQueryRoutes.cs               — GET /sessions, /sessions/{id}, /agents, /state
Settings/
  GlaudeHookSpec.cs                 — expected hook/statusLine entries for (port, exePath)
  HookEntry.cs                      — POCO/JsonNode model of one hook entry
  SettingsFile.cs                   — atomic load/save/backup of settings.json via JsonNode
  SettingsMerger.cs                 — install/detect/uninstall diff-merge engine (462 lines)
  StatusLineChain.cs                — capture/restore of pre-existing statusLine fields
Versioning/
  ClaudeVersion.cs, ClaudeVersionProbe.cs, CurlProbe.cs, VersionGate.cs
```

`tests/Glaude.Tests/` — 23 xUnit test files (~5,800 lines), ~1:1 mirroring of src files. Notably **no test exercises `MonitorForm.cs` itself** — only its pure WinForms-free companion `MonitorTreeBuilder.cs` is tested. Refactoring the UI layer directly has no regression safety net at the WinForms layer; the safety net is one level down.

Root-level docs (rich in decision rationale, worth reading before touching Settings/Metrics):
`README.md`, `project.md`, `project-plan.md`, `project-ui.md`, `project-ui-plan.md`.

### 1.2 UI framework detail

- `Glaude.csproj`: `Sdk="Microsoft.NET.Sdk.Web"`, `TargetFramework=net8.0-windows`, `UseWindowsForms=true`, `OutputType=Exe` (console subsystem stays visible alongside the Form), self-contained single-file publish, win-x64.
- **Zero `<PackageReference>` entries** in the main project — no MVVM toolkit, no terminal emulation package, no graph/diagram library. Everything is base SDK.
- `MonitorForm.cs`: single `Form`, header `Panel` (owner-painted columns), `TreeView` in `OwnerDrawAll` with a `DoubleBufferedTreeView` inner class (P/Invoke `TVS_EX_DOUBLEBUFFER`), bottom status `Label`. Built imperatively (`new Form()`, `Controls.Add(...)`), no designer file.
- Refresh model: `SessionState.Changed` event + `FileSystemWatcher` on `%USERPROFILE%\.claude\projects`, debounced 250ms, full tree rebuild with expand/selection-state preservation.

### 1.3 Session data model (`Metrics/SessionState.cs`)

```csharp
public sealed record SessionSnapshot(
    string SessionId, string? ModelId, string? ModelDisplayName, string? EffortLevel,
    long? ContextWindowSize, long? UsedTokens, double? UsedPercentage, double? RemainingPercentage,
    decimal? CostUsd, string? PayloadVersion, DateTime ReceivedAtUtc,
    string Source = "statusLine", bool Ended = false, string? SessionName = null);

public sealed record AgentRecord(
    string AgentId, string? AgentType, string? ParentSessionId, string? ModelId, string? EffortLevel,
    int InputTokens, int OutputTokens, int CacheCreationInputTokens, int CacheReadInputTokens,
    int ContextWindowSize, AgentStatus Status, DateTime ReceivedAtUtc, string Source, string? Name = null);

public enum AgentStatus { Live, Ended, Stale }
```

`SessionState` = `ConcurrentDictionary<string, SessionSnapshot>` + `ConcurrentDictionary<string, AgentRecord>`, in-memory only, `Changed` event, empties on every restart by design. "Running" is derived (disk enumeration vs in-memory map), not a stored bool. `SessionName` exists but is sourced only from live statusLine payload — never persisted.

### 1.4 Persistence inventory

| What | Where | Format |
|---|---|---|
| Root folders | `%USERPROFILE%\.claude\glaude-folders.json` (preferred) → `<exe dir>\folder.json` → `<cwd>\folder.json` | flat JSON string array, first-existing-file wins, no merge |
| Status-line chain originals | `%USERPROFILE%\.claude\glaude-state.json` | JSON, for uninstall restore |
| Claude Code hook config | `%USERPROFILE%\.claude\settings.json` | mutated via `JsonNode` DOM, `.glaude.bak` backup |
| Session list | **not persisted** | derived by enumerating `%USERPROFILE%\.claude\projects\<slug>\<session_id>.jsonl` + `<session_id>\subagents\agent-<agent_id>.jsonl`/`.meta.json`, root attribution via longest-matching `cwd` in transcript |
| Live in-memory state | `SessionState` | none, resets on restart |

## 2. Target UI spec (from requirements discussion)

### 2.1 Shell layout

```
┌─────────────────────────────────────────────────────────────┐
│ Menu bar                                                     │
├──────────┬─────────────────────────────────────┬────────────┤
│          │ [Tab1][Tab2][Tab3] ...          (C) │            │
│  Panel   ├─────────────────────────────────────┤   Panel    │
│    A     │                                     │     B      │
│ (roots + │        Session window (D)           │ (file/git  │
│ sessions │                                     │   tree)    │
│  list)   │                                     │            │
│          ├─────────────────────────────────────┤            │
│          │       Agent graph panel (E)          │            │
└──────────┴─────────────────────────────────────┴────────────┘
```

Standard WPF shell: `DockPanel`/`Grid` root, `Menu` docked top, `GridSplitter`-separated left/center/right columns, center column itself a `Grid` with row splitter between (C+D) and (E).

### 2.2 Panel A — roots &amp; sessions (left)

- List of registered root folders. Add (creates dir on disk if missing) / remove (dereference only, per requirement — does not delete the folder from disk).
- Per root: list of known Claude sessions — name (ellipsis-truncated per available width), model icon column, effort icon column. Hover tooltip: session id, context size (and likely: cwd, last-active time, token usage — implementation detail for addenda).
- Per-root actions:
  1. **Create session** — equivalent to running `claude` — opens a creation popup: model select, effort select, free-text extra CLI args (e.g. `--permission-mode bypassPermissions`).
  2. **Rename session** — equivalent to `/rename` (a slash command *inside* the interactive session, not a CLI flag — implies driving it through the PTY stdin, not out-of-band).
  3. **Remove session** — delete session + associated Claude user-data (under `%USERPROFILE%\.claude\projects\<slug>\<session_id>...`).
  4. Visual state: running vs closed (color/icon), focused vs unfocused (tied to tab selection (C)).
  5. **Resume session** — same result as create (2), i.e. spawns `claude --resume <id>` (or `-r`) and reopens as a tab.
  6. **Stop session** — kill the running `claude` process (candidate gesture: double-click, per requirement, needs confirmation UX since it's irreversible for the process).

### 2.3 Panel B — file/git tree (right)

- File/folder tree of the *selected* session's working directory, live while running.
- Explicitly scoped to grow git capabilities later (status badges, diff, etc.) — v1 is read-only tree.

### 2.4 Center — tabs (C) + session window (D) + agent graph (E)

- (C): one tab per open session (tab strip above D), switching sets "focused" session (drives A's focus highlight and B's tree).
- (D): must be "the exact same interactive window as running `claude` from cmd.exe" — i.e., a real interactive terminal hosting the actual `claude` CLI process (full keyboard input, ANSI colors, cursor control, resize-aware), not a log viewer. This is the single largest technical unknown — needs ConPTY + a terminal-emulation render surface, addressed in the addenda.
- (E): parent/child graph, root = current session (model, effort, context window, tokens, execution time), children = sub-agents with the same fields. Needs a WPF graph-layout approach — existing `RootsTreeBuilder`/`AgentRecord` data is structurally ready (parent/child via `ParentSessionId`), rendering is new.

## 3. Known technical unknowns for the addenda to resolve

1. **Interactive terminal hosting in WPF** — options to evaluate: ConPTY via P/Invoke + a VT100/ANSI parser and custom `FrameworkElement` renderer, vs. embedding via WebView2 + xterm.js talking to a ConPTY backend over a pipe/websocket, vs. any maintained WPF terminal control. Must feel identical to running `claude` in cmd.exe (colors, cursor movement, resize, Ctrl+C, etc.).
2. **How `claude` itself is actually invoked** — is it a node-based CLI shim, a native binary, on PATH as `claude.cmd`/`claude.exe`? Affects `Process.Start` + ConPTY attach details on Windows.
3. **Driving `/rename` and other slash commands** — since there's no non-interactive rename flag, rename must be executed by sending `/rename <name>\n` into the PTY's stdin of a live session, or by starting the session headless long enough to issue the command. Needs concrete verification against current `claude` CLI behavior.
4. **Session removal semantics** — what exactly under `%USERPROFILE%\.claude\projects\<slug>\<session_id>...` must be deleted, and whether Claude Code keeps other user-data (e.g. shell snapshots, todo caches) elsewhere that also needs clearing.
5. **Persisted session/root metadata store** — new schema needed (root path, display name overrides, pinned/favorite, last-opened) since today nothing is persisted beyond the root-path array.
6. **Graph layout library choice for panel E** — WPF has no built-in graph layout; needs an actual library recommendation (e.g. MSAGL, GraphX, or a hand-rolled layered-tree layout given the data is a simple parent/child hierarchy, not a general graph).
7. **Process lifecycle &amp; multi-session concurrency** — N tabs = N live `claude` child processes + N ConPTY instances; resource/cleanup story on app exit, crash recovery, orphaned process detection.
8. **MVVM architecture** — this is a ground-up rewrite from WinForms; needs ViewModel structure, command bindings, and how the existing `SessionState`/`MetricsPipeline`/event-server backend plugs into it (likely: backend keeps running as-is for telemetry into panels A/E; a new orchestration layer owns process lifecycle for panel D).

## 4. Technical Addendum — Opus Review

All `[VERIFIED-DISK]`/`[VERIFIED-CLI]` claims below were checked live on this machine
(`claude.exe` 2.1.232.0, `%USERPROFILE%` = `C:\Users\a.sintes`) on 2026-08-14, using the same
"verify, don't assume" discipline `project-ui.md` already applies.

### 4.1 How `claude` is actually invoked — settled

**[VERIFIED-CLI]** `where claude` → a single hit, `C:\Users\a.sintes\.local\bin\claude.exe`,
`CommandType = Application`, `FileVersion 2.1.232.0`, **319 MB**, no reparse point / symlink
target, and `~/.claude.json` reports `installMethod: native`. There is **no `.cmd` shim and no
`node` in the chain** — it is a single self-contained native PE with the JS runtime bundled.
Consequence: `Process.Start` targets the `.exe` directly. Do **not** wrap in `cmd.exe /c`
(breaks Ctrl+C signal ownership and adds a console host you then have to attach around), and do
**not** rely on `UseShellExecute`.

Two operational gotchas the code must handle:

- Self-update replaces the binary in place (`claude.exe.old.1786687145610` sits next to it
  **[VERIFIED-DISK]**). **Resolve the path per launch** (PATH probe, cached for at most the
  lifetime of one launch), never at app start.
- `.local\bin` is a user-PATH entry, so a `ProcessStartInfo` with an explicit `FileName` is
  preferable to relying on the child inheriting PATH resolution.

Verification to re-run on any target machine before shipping (cheap, put it behind
`glaude doctor`):

```powershell
Get-Command claude -All | Format-List Name,CommandType,Source,Version
# if CommandType is Application  -> spawn the .exe directly (this machine)
# if it resolves to a .cmd/.ps1  -> read it; a node shim means spawning node.exe + the .js
#                                   entry, because ConPTY-attaching a batch shim gives you
#                                   cmd.exe's console, not claude's
```

### 4.2 Interactive terminal hosting — build WebView2 + xterm.js over ConPTY first

**Recommendation: WebView2 hosting xterm.js, talking to a ConPTY backend over a loopback
WebSocket served by the EventServer that already exists.** Build this for the MVP.

The decisive argument is not "xterm.js is a good emulator" (it is — it is the emulator in VS
Code, so it is battle-tested against exactly the DEC private modes, alternate screen buffer,
bracketed paste, OSC-8 hyperlinks, and 24-bit SGR sequences that Claude Code's TUI actually
emits). The decisive argument is that **the transport is already built**: `Server/EventServer.cs`
is an ASP.NET Core minimal-API Kestrel host bound to loopback, and the csproj is already
`Microsoft.NET.Sdk.Web`. Adding one endpoint is a ~40-line change, and it reuses the existing
"one long-lived server object per process, routes are thin readers over it" pattern that
`RootsRoutes`/`StateQueryRoutes` established:

```csharp
// Server/PtyRoutes.cs — symmetry with RootsRoutes.cs
app.Map("/pty/{tabId}", async (string tabId, HttpContext ctx, PtyRegistry ptys) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    if (!ptys.TryGet(tabId, out var pty)) { ctx.Response.StatusCode = 404; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await pty.PumpAsync(ws, ctx.RequestAborted); // bytes both ways, plus a {"resize":[c,r]} frame
});
```

The ConPTY side is a small, self-contained P/Invoke surface — five entry points, no library
needed:

```csharp
[DllImport("kernel32.dll", SetLastError = true)]
static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput,
                                     uint dwFlags, out IntPtr phPC);
[DllImport("kernel32.dll", SetLastError = true)]
static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
[DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr hPC);
// + CreatePipe, InitializeProcThreadAttributeList /
//   UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016),
//   then CreateProcessW with EXTENDED_STARTUPINFO_PRESENT.
```

`ResizePseudoConsole` is what makes the panel resize-aware, and it is the single feature that
kills every "just redirect stdout" alternative: without a real PTY, Claude Code sees a
non-tty, disables its TUI, and you get a log viewer — precisely what the requirement forbids.
Ctrl+C works because the child owns the pseudo-console: forward the raw `0x03` byte down the
input pipe rather than trying `GenerateConsoleCtrlEvent`.

Costs to accept honestly: two NuGet packages where there are currently zero
(`Microsoft.Web.WebView2`, plus `Microsoft.Extensions.*` already transitively present), an
Evergreen WebView2 runtime dependency (present on all supported Windows 11 by default; add a
`glaude doctor` probe), and the fact that `IncludeNativeLibrariesForSelfExtract=true` is
already set in `Glaude.csproj`, so `WebView2Loader.dll` survives single-file publish. xterm.js
+ its addons ship as ~1 MB of `EmbeddedResource` served from a tiny in-process static-file
route — no CDN, no network dependency, no npm at build time.

**Fallback / harder path** (only if WebView2 is vetoed, e.g. an enterprise policy blocking the
runtime): a hand-rolled VT parser plus a custom `FrameworkElement` rendering a cell grid with
`GlyphRun`s and a `WriteableBitmap`-backed damage model. Budget this at **weeks, not days** —
you are re-implementing scrollback, wide/combining-character handling, alternate screen buffer,
mouse reporting (SGR 1006), and DECSCUSR cursor shapes, and every gap shows up as visible
corruption in the one panel the user stares at. Do not start here. A middle option, embedding
Windows Terminal's `TerminalControl` via XAML Islands, is explicitly **not** recommended: it is
not shipped as a supported redistributable control, and it drags WinUI3/WinAppSDK into a
WinForms-heritage single-file app.

### 4.3 Driving `/rename` and other slash commands

The spec's premise is half wrong, and the correction removes most of the risk.
**[VERIFIED-CLI]** `claude --help` exposes `-n, --name <name>` — "Set a display name for this
session (shown in the prompt box, /resume picker, and terminal title)". So **naming at
creation needs no PTY typing at all**; it is a launch argument. Equally important,
`--session-id <uuid>` lets Glaude **choose the session id before the process starts**, which
removes the otherwise-nasty race of correlating a new tab with a transcript file / a
`SessionState` key. Take it:

```
claude --session-id <guid-we-generated> --name "<display name>" --model opus --effort high \
       [--resume <id> [--fork-session]] [extra user args…]
```

Renaming a **live** session still has to go through the PTY, and the safe mechanism is:

1. Refuse to inject unless the session is idle. `%USERPROFILE%\.claude\sessions\<pid>.json`
   **[VERIFIED-DISK]** carries `{"pid":47440,"sessionId":"…","cwd":"C:\\projects","name":
   "projects-ca","nameSource":"derived","status":"busy","statusUpdatedAt":…}`. We spawned the
   process, so we know the pid; `status` is the gate.
2. Write `"/rename <name>"` then a lone `\r` (not `\n`) into the ConPTY input pipe, as two
   separate writes with a short gap, so the TUI's slash-command autocomplete settles.
   Sanitise ruthlessly first: reject anything containing `\r`, `\n`, `\x1b`, or `\x03` — a
   newline in a "display name" is a command-injection primitive against the user's own agent.
3. Detect completion by **file state, never by scraping the terminal**: poll (or
   `FileSystemWatcher`) `sessions/<pid>.json` until `name` equals the requested value and
   `nameSource` flips off `derived`, with a 5 s timeout → surface a "rename may not have
   applied" non-modal warning. Screen-scraping a TUI for confirmation is the thing that will
   break on every Claude Code release.

Same three-step shape generalises to any future slash command; make it one
`SlashCommandDriver.InvokeAsync(command, args, completionPredicate, timeout)` so each command
declares its own file-state completion predicate.

Note for panel A's name column: **[VERIFIED-DISK]** the transcript itself persists the display
name as `{"type":"ai-title","aiTitle":"Refactor Glaude UI to modern WPF application",
"sessionId":"…"}` lines (last one wins). That is a strictly better name source for *ended*
sessions than `RootsTreeBuilder`'s current `first_message` derivation — worth adding as a new
`name_source: "ai_title"` ranked above `first_message` in `BuildSessionDto`, cached with the
same (length, mtime) key the tail cache already uses.

### 4.4 "Remove session" — exact deletion set

**[VERIFIED-DISK]** on this machine, a session id `S` with cwd slug `<slug>` leaves traces in
six places. Delete, in this order (most-specific first, so a failure part-way leaves the
transcript as the recoverable anchor — actually invert it: delete the transcript **last**, so
an aborted removal is still visible in the UI rather than becoming an orphaned pile of caches):

| # | Path | Notes |
|---|---|---|
| 1 | `~/.claude/file-history/<S>/` | recursive; contents are `<hash>@v1..@vN` file backups |
| 2 | `~/.claude/tasks/<S>/` | recursive; contains `.highwatermark`, `.lock` |
| 3 | `~/.claude/session-env/<S>/` | recursive |
| 4 | `~/.claude/projects/<slug>/<S>/` | recursive — the `subagents/agent-<id>.jsonl` + matching `.meta.json` sidecars `MetaJsonReader` reads |
| 5 | `~/.claude/history.jsonl` | **rewrite, don't delete** — shared append-only prompt log, one JSON object per line each carrying `"sessionId"`; filter out lines where `sessionId == S` |
| 6 | `~/.claude/projects/<slug>/<S>.jsonl` | the transcript; delete last |

Explicitly **do not touch**:

- `~/.claude/shell-snapshots/snapshot-bash-<epochms>-<rand>.sh` — **[VERIFIED-DISK]** the
  filenames carry no session id and the contents are a generic shell dump, so there is no safe
  mapping. Attributing them by mtime proximity would delete another session's snapshot. Leave
  them; they are small and Claude Code has its own `.last-cleanup` sweep.
- `~/.claude/paste-cache/<contenthash>.txt` — content-addressed and therefore potentially
  **shared between sessions**. Deleting one could blank a paste in a surviving session.
- `~/.claude/sessions/<pid>.json` — pid-keyed, and **[VERIFIED-DISK]** only present while the
  process lives (one file for the one running session). Removing a session must kill the
  process first and let Claude Code clean this up; only force-delete it if the pid is dead and
  its `sessionId` field equals `S`.
- `~/.claude.json` — `projects["<cwd>"].lastSessionId` **[VERIFIED-DISK]** may now dangle.
  That is harmless (Claude Code tolerates a stale id) and this file is huge, hot, and
  concurrently written by every live session. Do not rewrite it.

Safety harness, non-negotiable given this deletes real user data:

- Every target path is validated to be (a) under `%USERPROFILE%\.claude\`, and (b) a leaf whose
  final segment is *exactly* the session GUID (`Guid.TryParseExact(name, "D")`), before any
  `Directory.Delete(recursive: true)`. Reject reparse points (`FileAttributes.ReparsePoint`) so
  a planted junction cannot redirect the recursive delete outside `.claude`.
- Two-phase: build the full plan, show it in the confirmation dialog (paths + total bytes),
  then execute. Log every deleted path.
- Default the whole feature to **recycle-bin move**, not hard delete, with a "permanently
  delete" checkbox. `Shell32`'s `SHFileOperation`/`FOF_ALLOWUNDO` is the cheap way; this alone
  converts "catastrophic bug report" into "user restores from bin".
- Unit-test it exactly as `RootsTreeBuilder` is tested — against a fixture `.claude` tree via a
  `homeDirOverride` parameter, never the real profile.

### 4.5 Persisted root/session metadata schema

Keep the file at `%USERPROFILE%\.claude\glaude-folders.json` and keep `RootFoldersConfig`'s
three-candidate probe order and its "first file that exists decides, malformed → empty, never
throw" contract. Make the **reader** polymorphic on the JSON root token, so every existing
`["C:/projects"]` file keeps working untouched and a v1 file is upgraded in place on first
write:

```json
{
  "version": 2,
  "roots": [
    {
      "path": "C:/projects",
      "display_name": null,
      "pinned": true,
      "added_utc": "2026-08-14T06:00:00Z",
      "last_opened_utc": "2026-08-14T06:36:41Z",
      "default_launch": { "model": "opus", "effort": "high", "extra_args": [] }
    }
  ],
  "sessions": {
    "a7401e4f-b4da-47cf-8ce6-c293332e89dd": {
      "display_name": "Glaude WPF refactor",
      "pinned": false,
      "last_opened_utc": "2026-08-14T06:36:41Z",
      "hidden": false
    }
  }
}
```

Design rules that matter:

- `ValueKind == Array` → v1 path: map each string to a `RootEntry` with all-default metadata.
  `ValueKind == Object` → v2. Anything else → empty, exactly as today. `RootFoldersConfig.Load`
  keeps returning `string[]` as a thin shim over the new loader so `EventServer.Roots`,
  `RootsRoutes`, and `RootsTreeBuilder.Build` need **zero** changes.
- `sessions` is a **sparse override map keyed by session id** — never a session registry.
  Sessions stay derived from disk (that invariant is the whole reason `RootsTreeBuilder` is
  cheap and self-healing); this map only holds the handful of things Glaude owns and Claude
  Code does not. Name resolution order becomes: Glaude override → live `statusLine`
  `SessionName` → transcript `aiTitle` → first message → truncated id.
- `path` is stored **verbatim** as the user typed it (project-ui.md's rule) with normalisation
  only for comparison — so `Path.GetFullPath` output never leaks into the file.
- Writes are atomic and backed up with the same mechanism `Settings/SettingsFile.cs` already
  implements (temp file + `File.Replace` + `.glaude.bak`). Reuse it; do not write a second
  atomic-save helper.
- Prune `sessions` entries whose session no longer exists on disk on save, so the file cannot
  grow without bound.

### 4.6 Panel E graph — hand-roll the layout

**Recommendation: hand-rolled layered layout, ~150–250 lines, no library.**

The data is not a graph. `AgentRecord.ParentSessionId` gives a **strictly two-level tree** —
one session root, N sub-agent children — and `RootsTreeBuilder.AttachAgents` already only
nests live agents one level under a live session. Observed fan-out on this machine is 3
subagents for the busiest session **[VERIFIED-DISK]**; call it ≤ 30 nodes worst case.

- **MSAGL** solves the general Sugiyama layered-DAG problem with edge routing and crossing
  minimisation. For a two-level star it is a ~1.5 MB dependency computing an answer you can
  write as one arithmetic expression, and its WPF viewer (`AutomaticGraphLayout.WpfGraphControl`)
  brings its own zoom/pan/selection model that will fight WPF data binding and your styling —
  you would end up drawing nodes as MSAGL objects, not as templated `DataTemplate`s carrying
  model/effort/token/duration fields, which is precisely what the requirement asks for.
- **GraphX** is a .NET-Framework-era wrapper over QuickGraph, effectively unmaintained, and
  would be the only unmaintained dependency in a fresh net8 app.
- **Hand-rolled** fits Glaude's established architecture exactly: a *pure*, WPF-free
  `AgentGraphLayout.Compute(AgentGraphModel) → AgentGraphLayoutResult` (node rects + edge
  segments), unit-tested with no UI, sitting beside `MonitorTreeBuilder`/`MonitorColumnLayout`
  which are *already* pure geometry-computing companions to a UI class and *already* the only
  parts of the UI layer with test coverage. Rendering is an `ItemsControl` over a `Canvas` with
  `Canvas.Left/Top` bound to the layout result, plus one `Path` per edge; each node is a
  `DataTemplate` showing model icon, effort, context-window gauge, tokens, elapsed time.

Layout is trivial: root centred at top, children on one row beneath, x-spaced by
`(i + 0.5) * columnWidth`, wrapping to a second row past ~8 children, cubic-Bézier edges from
root-bottom to child-top. Add zoom/pan later with a `ScaleTransform` on the canvas if anyone
asks. If fan-out ever becomes genuinely deep and irregular, revisit — but pay that cost then,
with a pure layout function you can swap behind an unchanged interface.

### 4.7 MVVM architecture

The load-bearing idea: **two independent layers over the same session identity, joined only by
the session id.** The existing backend becomes a *read-only telemetry side*; everything that
owns a process is new and never mutates telemetry state.

```
src/Glaude/                     (unchanged; stays Microsoft.NET.Sdk.Web + net8.0-windows)
  Metrics/, Server/, Settings/, Versioning/     ← telemetry read side, untouched
  Server/PtyRoutes.cs                           ← NEW: loopback WebSocket per PTY tab
  Orchestration/                                ← NEW: process/PTY ownership
    ClaudeCliLocator.cs        resolve claude.exe per launch (§4.1)
    ClaudeLaunchSpec.cs        record: SessionId, Model, Effort, Cwd, ResumeOf, ExtraArgs
    ConPty.cs                  P/Invoke wrapper, IDisposable
    PtySession.cs              one live process + its ConPTY + byte pumps
    PtyRegistry.cs             tabId -> PtySession, app-lifetime singleton
    SlashCommandDriver.cs      §4.3
    SessionRemover.cs          §4.4 (plan / confirm / execute, homeDirOverride for tests)
  App/                                          ← NEW: WPF
    App.xaml, MainWindow.xaml, Views/*.xaml, Controls/TerminalView.xaml (WebView2)
    ViewModels/  ShellViewModel, RootsPanelViewModel (A), FileTreePanelViewModel (B),
                 TabsViewModel (C), TerminalTabViewModel (D), AgentGraphViewModel (E)
    Services/    ISessionSelectionService, ITelemetryFeed, IDialogService
    Layout/      AgentGraphLayout.cs  ← pure, testable (§4.6)
```

Keep it as **one project, one process**, mirroring project-ui.md's explicit "no new
project — the existing `Glaude.csproj` grows a TFM" decision. Add
`<UseWPF>true</UseWPF>` alongside the existing `UseWindowsForms=true` (both are supported
simultaneously on `net8.0-windows`), keep `MonitorForm` reachable behind the old verb during
the transition, and delete it once panel A reaches parity. That preserves the hooks/statusline
install path, the Kestrel host, and the whole existing test suite unchanged — which matters,
because §1.1 notes the WinForms layer has no regression net, so the migration must not also be
a backend migration.

Communication, concretely:

- Take **CommunityToolkit.Mvvm** (source-generated `[ObservableProperty]`/`[RelayCommand]`,
  plus `WeakReferenceMessenger`). It is the only sane default in 2026 and its weak messenger
  removes the leak class that a hand-rolled event bus invites across five long-lived panels.
- **`ISessionSelectionService`** is the single source of truth for "focused session id",
  exposing `string? FocusedSessionId` + a change notification. `TabsViewModel` (C) is the only
  writer. A (highlight), B (file tree root ← the session's `cwd`, which
  `TranscriptHeadReader` already resolves), and E (graph root) are readers. Point-to-point
  bindings between panels are banned; this service is the hub.
- **`ITelemetryFeed`** wraps the existing push model rather than replacing it: subscribe to
  `SessionState.Changed` and the `%USERPROFILE%\.claude\projects` `FileSystemWatcher`, reuse
  `DebounceCoalescer` verbatim at 250 ms, and marshal to the UI thread with
  `Dispatcher.BeginInvoke` in place of `Control.BeginInvoke`. It then calls
  `RootsTreeBuilder.Build(...)` exactly as `MonitorForm.RefreshAndRender` does today and
  publishes one `RootsTreeUpdated` message. A and E rebuild from that; nobody else reads
  `SessionState` directly. Do **not** switch to HTTP polling of `/roots/tree` — in-process
  push already works and the routes stay for external consumers.
- **`PtyRegistry` is app-lifetime and owns disposal**, not the tab ViewModels. One tab close →
  `PtySession.Dispose()` → `ClosePseudoConsole` + close pipes + wait-with-timeout on the child
  → `Process.Kill(entireProcessTree: true)` on timeout (Claude Code spawns MCP servers and
  `Bash` children; `entireProcessTree` is what stops the orphan pile). App exit → dispose the
  registry inside a `try/finally` around `Application.Run`, plus a
  `SetConsoleCtrlHandler`/`AppDomain.ProcessExit` belt-and-braces pass. On next startup,
  reconcile: read every `~/.claude/sessions/<pid>.json`, and for each, if the pid is dead or
  `procStart` does not match, treat the entry as stale — that file is the machine-wide
  orphaned-`claude` detector the spec's unknown #7 was missing.
- Resource ceiling to state up front: N tabs = N `claude.exe` processes, each of which is a
  full Node-class runtime plus its MCP children. Cap open terminal tabs (8 is a sane default,
  configurable) and surface the count; this is a product constraint, not a bug to fix later.

## 5. Technical Addendum — Fable Review

### 5.1 Process lifecycle & multi-session concurrency

N tabs = N `claude` children, each behind its own ConPTY (`CreatePseudoConsole` + one pipe pair per tab; ConPTY is per-console, not shareable). Recommendation:

- **One Job Object for the whole app**, created at startup with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, every spawned `claude` assigned via `AssignProcessToJobObject` immediately after `CreateProcess` (start suspended, assign, resume — avoids the race where the child spawns grandchildren before assignment). This alone covers both normal exit and hard crash: the OS closes the job handle when Glaude dies, killing the entire child tree (including node.exe grandchildren under a `claude.cmd` shim — the job captures descendants, which a plain `Process.Kill(entireProcessTree)` on crash cannot, because nobody is alive to call it).
- **Normal exit**: on window close, iterate live `TerminalSessionController`s: send Ctrl+C / close the PTY input pipe, wait ~2s for graceful exit (lets `claude` flush its transcript and fire `SessionEnd` — which our own `EventServer` consumes), then `ClosePseudoConsole` + `TerminateProcess`. Never rely on the job alone for the graceful path; abrupt kill can leave a truncated last `.jsonl` line (TranscriptReader already tolerates partial lines, so this degrades, not breaks).
- **PID registry as belt-and-suspenders**: `%USERPROFILE%\.claude\glaude-sessions.json` (same tolerant-JSON conventions as `glaude-state.json`/`RootFoldersConfig`): array of `{sessionId, pid, processStartTimeUtc, cwd, launchedAtUtc}`. Written on spawn, entry removed on reap. On startup, reconcile: a PID is "ours and orphaned" only if the PID exists **and** its `Process.StartTime` matches the recorded start time (PID reuse guard). With KILL_ON_JOB_CLOSE the registry should always be empty at startup; if not, offer "kill orphan / adopt as detached" in a dialog. Registry writes must be atomic (temp+rename, as `SettingsFile` already does).
- **Reaping**: subscribe `Process.Exited` (EnableRaisingEvents) per child; on exit, flip the tab's `SessionViewModel.IsRunning` = false, keep the tab open showing the frozen scrollback plus an exit banner. `SessionState` liveness stays hook-driven; the process-exit signal is a second, more authoritative source for sessions Glaude itself owns — feed it into the same `Changed` pipeline.

### 5.2 Panel A visual states

Use **WPF-UI (lepo.co)** for Fluent theming — it's the lightest way to get a modern look and a `SymbolIcon` set; everything below still works with plain styles if the dependency is rejected. Four states from two bools on `SessionViewModel` (`IsRunning`, `IsFocused` — focused implies a selected tab in C):

| State | Leading glyph | Name text | Row background |
|---|---|---|---|
| Running + focused | filled circle, accent color (`SystemAccentColor`) | SemiBold, primary text brush | subtle accent tint (accent at ~15% opacity) + 3px accent left border |
| Running + unfocused | filled circle, green (#4CAF50) | SemiBold, primary | transparent |
| Closed + selected in A | hollow circle, gray | Normal, secondary (60% opacity) | standard `ListView` selection brush |
| Closed | hollow circle, gray | Normal, secondary | transparent |

Expressed as plain `DataTrigger`s in the item `ControlTemplate`/`ItemContainerStyle`: base setters = closed look; `<DataTrigger Binding="{Binding IsRunning}" Value="True">` sets glyph fill/green + SemiBold; a `MultiDataTrigger` on `IsRunning=True` + `IsFocused=True` layers the accent tint + left border. Never color-only (same rule `MonitorForm.ApplyStyle` already follows): the glyph shape (filled vs hollow) and weight carry the state too. Add `AutomationProperties.Name` including "running/stopped" for accessibility.

### 5.3 Model/effort icon columns

`ModelWindowTable` has no visual concept — add a parallel `ModelBadgeTable` (same prefix-match resolution, reused) mapping model family → badge. Convention: **letter badge in a rounded 16×16 chip**, colored by family, letter always shown (color-blind safe):

- Opus: `O`, purple (#8E44AD) — flagship.
- Sonnet: `S`, blue (#2D7FF9).
- Haiku: `H`, teal (#1ABC9C).
- Fable: `F`, amber (#E67E22).
- Unknown/prefix-unmatched: `?`, gray — mirrors `Resolve(out matched)`'s "assumed" semantics; tooltip shows the raw model id (dated ids like `claude-haiku-4-5-20251001` resolve to family by prefix).

Effort column: **1–4 stacked bars** (like signal strength), monochrome secondary brush, filled count = low/medium/high/max(+xhigh=4 filled + dot); null effort renders an em-dash. Both are tiny `UserControl`s bound to `ModelFamily`/`EffortLevel` enum properties on the VM; tooltips give the full words. This keeps the columns ~20px wide, matching the ellipsis-truncated name column requirement.

### 5.4 Migration phases

Backend (`Server/`, `Metrics/`, `Settings/`, `Versioning/`) is untouched throughout; each phase ships a runnable app.

- **Phase 1 — WPF shell + read-only panel A.** New `net8.0-windows` WPF project (or `UseWPF=true` alongside; drop WinForms only at the end), MVVM skeleton, panel A bound to `RootsTreeBuilder` DTOs via the same in-process `EventServer` + debounce pattern `MonitorForm` uses. Root add/remove (writes `glaude-folders.json`). All 23 test files stay valid — `MonitorTreeBuilder` tests keep passing but now cover dead-weight code; keep them until Phase 3 confirms the DTO mapping, then retire `MonitorTreeBuilder`/`MonitorTreeExpansion` tests and port their expansion-preservation cases to the new VM layer.
- **Phase 2 — PTY terminal MVP, single session, no tabs.** ConPTY host + VT parser/renderer (or WebView2+xterm.js — decide here, this is the risk spike), "create session" popup, job object + PID registry from 5.1. New tests: PTY controller lifecycle, registry round-trip, ANSI parser corpus (captured real `claude` output). No existing tests affected.
- **Phase 3 — tabs + multi-session.** Tab strip (C), `IsFocused` wiring into panel A, N-process lifecycle. Tests: concurrency/reap tests; existing backend tests untouched.
- **Phase 4 — rename/remove/resume/stop.** Resume = `claude --resume <id>` into a new PTY; rename = `/rename` via PTY stdin (verify per unknown #3); remove guarded per 5.5. First phase needing new deletion-semantics tests (fixture projects dir, reuse `ProjectsDirOverride`).
- **Phase 5 — panel B read-only file tree** (per-session cwd + FileSystemWatcher). **Phase 6 — panel E agent graph** (hand-rolled layered tree over `AgentRecord.ParentSessionId` — it's a strict hierarchy, no MSAGL needed). **Phase 7 — git in panel B** (status badges via `git status --porcelain=v2`, no libgit2sharp dependency initially). Phases 5–7 are independent of each other once 3 lands; none touch existing tests.

### 5.5 Risk register

1. **ConPTY encoding/resize edge cases** (UTF-8 partial sequences across reads, `ResizePseudoConsole` reflow) — spike in Phase 2 with a scripted resize/emoji corpus; decode with a stateful `System.Text.Decoder`, never per-chunk `GetString`.
2. **ANSI/VT parser correctness under real `claude` output** (sync-update DEC 2026, alt-screen, OSC titles) — strongly favor WebView2+xterm.js over a hand-rolled parser; if hand-rolled, gate on a recorded-output replay test corpus.
3. **"Remove session" deleting a live session's data** — hard rule: remove is disabled (command `CanExecute=false`) while `IsRunning` or while the PID registry has a live entry for that id; deletion path re-checks liveness immediately before `Directory.Delete` and moves to a trash dir first (undoable), never direct delete.
4. **Terminal not matching cmd.exe rendering** (font metrics, box-drawing, cursor) — pin Cascadia Mono, integer cell metrics, and validate side-by-side with a real console early; xterm.js again largely buys this outright.
5. **ProcessId ownership across restarts / PID reuse** — never trust a bare PID: registry stores (PID, process start time) pairs and validates both; job object with KILL_ON_JOB_CLOSE makes stale entries an anomaly to surface, not a state to manage.

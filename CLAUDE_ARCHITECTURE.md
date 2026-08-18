# Accel — Software Architecture

This document describes the software architecture of Accel: components, responsibilities, data/control
flow, key abstractions, and cross-cutting design decisions. It intentionally does not cover coding style
(see `CLAUDE_DESIGN.md`), folder layout/build/test mechanics (see `CLAUDE_ENV.md`), or end-user features
(see `README.md`).

Project: `accel.csproj`, SDK `Microsoft.NET.Sdk.Web`, `net8.0-windows`, `UseWPF` + `UseWindowsForms` both
enabled, `win-x64`, self-contained single-file publish. It uses the Web SDK (for the in-process Kestrel
server) even though there is no ASP.NET MVC — see the csproj comments about
`GenerateMvcApplicationPartsAssemblyAttributes` and the implicit-usings clash between the Web and
WindowsDesktop SDK overlays. `tests\accel.Tests` is a sibling project in `accel.sln`; `Accel.Tests` is
granted `InternalsVisibleTo` for testing internal P/Invoke marshalling (ConPTY structs) without exposing
it as public API.

## 1. High-Level Overview / Process Model

Accel is **one process** that does three things at once, composed manually in `Program.cs` — there is no
DI container and no separate server/UI processes talking over HTTP to each other:

1. **Hook installation** — best-effort merge of Accel's hook/statusLine entries into Claude Code's
   `~/.claude/settings.json` (`Settings/`, driven by `Cli/InstallCommand.cs`).
2. **A local HTTP server** — an in-process Kestrel instance (`Server/EventServer.cs`) bound to
   `http://127.0.0.1:<port>` (default port `EventServer.DefaultPort` / `AccelHookSpec.DefaultPort`,
   never `0.0.0.0`) that receives hook-event POSTs from Claude Code CLI sessions and serves state/PTY
   WebSocket routes.
3. **A WPF monitor UI** — a single `MainWindow` showing, across five panels (A–E): a tree of tracked
   project folders/sessions/agents plus the focused session's MCP-tool/Skill hit counts (panel A), a
   read-only file tree and a `git status` list for the focused folder (panel B), a tab strip of live PTY
   sessions (panel C), an embedded terminal (panel D via WebView2 + xterm.js), and a left-to-right node
   graph of the focused session's running sub-agents (panel E).

`Program.cs` also acts as a **CLI dispatcher**: Claude Code itself invokes the *same* `accel.exe` as a
short-lived child process for two verbs — `accel statusline` and `accel subagent-statusline` — which do
**not** start the server or UI; they POST once to a (possibly different, already-running) Accel process's
HTTP server and print/relay a status line to stdout. `accel doctor`/`accel --uninstall` are also
short-lived, UI-less invocations. Only the no-argument invocation (`Verb.Start`) runs the combined
install+server+UI mode described above (`RunCombinedAsync`).

There are also six hidden dev-only verbs (`pty-smoke-test`, `pty-session-smoke-test`,
`pty-registry-stress-test`, `pty-shutdown-orphan-test`, `terminal-e2e-smoke-test`, `tabs-e2e-smoke-test`)
checked by raw string comparison in `Program.cs` *before* `Cli/ArgParser.Parse` runs, so they never
collide with the documented verb surface. They exercise `Orchestration`/`App` smoke/stress harnesses
directly (real child processes, real WebView2/WPF) rather than being unit tests.

### Startup sequence (`RunCombinedAsync`, in order)

1. `InstallCommand.Run(port, ...)` — best-effort; a refusal only warns, never aborts startup.
2. `new EventServer()` → `server.BuildApp(port, dumpRawDir)` → `await app.StartAsync()` (Kestrel started
   non-blocking so `Main` can later join the UI thread and still call `StopAsync`).
3. A dedicated **STA thread** builds the WPF composition root by hand: `Accel.App.App()`,
   `WpfUiThreadDispatcher`, `TelemetryFeed` wired directly to the in-process `EventServer` instance (no
   HTTP/polling), `SessionSelectionService` (write access handed exclusively to `TabsViewModel` via
   `AcquireWriter()`), `Orchestration.PtyRegistry()`, `RootsPanelViewModel`, `TabsViewModel`, and finally
   `MainWindow`.
4. `Window.Loaded` starts `RootsPanelViewModel.Start()`; `Window.Closed` disposes `TabsViewModel`
   (closes every PTY session created via "Create session"), the panel stub view models, and the
   `TerminalView`'s WebView2 control.
5. `Console.CancelKeyPress` dispatches `window.Close()` onto the UI thread (rather than letting the CLR
   tear down mid-request) so Kestrel can still stop cleanly.
6. Main thread `uiThread.Join()`, then `await server.StopAsync()`.

## 2. Major Components

### 2.1 `Cli/` — process entry, verb dispatch, install/uninstall/doctor, monitor-tree projection

- **`ArgParser.cs`** — hand-rolled, never-throwing argv parser. First non-`--` token is the verb
  (`Start`/`StatusLine`/`SubagentStatusLine`/`Notify`/`Doctor`/`Unknown`); flags: `--port <n>`,
  `--uninstall`, `--dump-raw <dir>`; `Notify` additionally carries a `--route <path>` value.
- **`AccelPaths.cs`** — static path/exe-resolution helpers used so CLI verbs stay unit-testable:
  `DefaultSettingsPath()` (`%USERPROFILE%\.claude\settings.json`, user-scope only), `CurrentExePath()`
  (so installed hooks call the real binary, not `dotnet`), `SafeProbe(...)` (wraps a version probe so
  exceptions never escape to a CLI verb).
- **`InstallCommand.cs`** — loads `settings.json` via `Settings.SettingsFile`; refuses if not
  `IsWritableForInstall`; probes Claude Code's version (`Versioning`) to decide which optional hooks to
  register; builds an `AccelHookSpec`; calls `SettingsMerger.Detect` then `SettingsMerger.InstallInto`.
- **`UninstallCommand.cs`** — mirror of install: loads settings, calls `SettingsMerger.Uninstall`, saves
  if changed.
- **`DoctorCommand.cs`** — two independent, never-throwing preflight checks: resolving `claude` on PATH
  (native exe vs. shim — a shim means the ConPTY launch path can't `CreateProcess` into it directly and
  must spawn `node.exe` + JS entry instead) and probing the installed WebView2 Evergreen runtime version
  from the registry (needed by the terminal panel).
- **`StatusLineCommand.cs`** / **`SubagentStatusLineCommand.cs`** — the two verbs Claude Code itself
  invokes as short-lived children. `StatusLineCommand`'s hard invariant: stdout **is** the rendered status
  bar, so it must always print something non-empty and exit 0. It POSTs the raw stdin payload to
  `/events/status-line` fire-and-forget (never awaited before writing output), re-invokes the previously
  captured original `statusLine` command via `ShellCommandRunner` under a budget, and falls back to a
  synthesized or hardcoded line if nothing usable returns. `SubagentStatusLineCommand` is a pure observer:
  POSTs to `/events/subagent-status-line` and always prints nothing/returns 0 so Claude Code's default
  per-row rendering is preserved.
- **`MonitorTreeBuilder.cs`** — pure translator from the `Accel.Metrics` wire DTOs (`RootsTreeDto` etc.)
  into the UI-facing `MonitorTree`/`MonitorRootNode`/`MonitorSessionNode`/`MonitorAgentNode` shape that
  `App/ViewModels/RootsPanelViewModel.cs` renders. Also owns `MonitorTreeExpansion`, pure WPF-agnostic
  logic for preserving/computing TreeView expand-state across full-tree rebuilds, keyed on stable ids
  (root path / session id / agent id, never node index).
- **`FileBackedStatusLineChainStore.cs`** — implements `Settings.IStatusLineChainStore` by persisting
  captured original `statusLine`/`subagentStatusLine` commands to `%USERPROFILE%\.claude\accel-state.json`
  — needed because `install` (which captures) and `statusline`/`subagent-statusline` (which need to chain
  to the capture) are separate short-lived process invocations.
- **`NotifyCommand.cs`** — the `notify` verb: Accel's own replacement for the old `curl.exe`-based hook
  commands. Claude Code invokes `accel.exe notify --port <n> --route <path> -H "X-Accel-Hook: <Event>"`
  once per lifecycle event (see `Settings/AccelHookSpec.cs`), reads the payload from stdin, POSTs it to
  `http://127.0.0.1:<port><route>`, and always exits 0 having printed nothing — swallowing a connection
  refusal (Accel not running yet) is the whole point, since `curl.exe -s` failing silently used to surface
  as a "hook error" at every session start.
- **`DebounceCoalescer.cs`** — pure, timer-framework-agnostic debounce primitive (`Signal()`/`Elapsed()`)
  reused by `App/Services/TelemetryFeed.cs`.
- **`ShellCommandRunner.cs`** — runs an arbitrary shell command *string* (not exe+argv) with a hard
  timeout, needed because Claude Code's `statusLine` setting has no exec form. Pumps stdout/stderr/stdin
  concurrently to avoid pipe deadlock; on timeout kills the whole process tree.

### 2.2 `Settings/` — merging Accel's hooks into Claude Code's `settings.json`

Two independently modeled, never-conflated mechanisms: (1) `hooks` **event entries** (exec-form,
self-invoked `accel.exe notify` calls) and (2) the top-level `statusLine`/`subagentStatusLine`
**fields**.

- **`AccelHookSpec.cs`** — the complete expected set of Accel's settings.json entries for a given
  `(port, exePath)`. Always includes `SessionStart`/`SessionEnd`/`SubagentStop`; conditionally includes
  `SubagentStart` when version-gated support is present. Builds each hook as
  `HookEntry{Command=<exePath>, Args=["notify", "--port", <port>, "--route", <route>, "-H",
  "X-Accel-Hook: <Event>"]}` — Accel notifies *itself* rather than shelling out to `curl.exe`, so
  `Cli/NotifyCommand.cs` can swallow every failure (Accel not running yet, connection refused, ...)
  and always exit 0, instead of a bare `curl -s` failing non-blocking-but-visibly whenever Accel
  isn't up when a session starts. `statusLine`/`subagentStatusLine` fields point back at Accel
  itself too (`"<exePath>" statusline --port <port>`), since there's no exec form for those.
- **`HookEntry.cs`** — always exec form (never shell form) to avoid quoting ambiguity/injection.
  Ownership of a hook entry is decided **purely** by presence of the `X-Accel-Hook: <Event>` marker header
  arg — never by assuming Accel owns a whole matcher group or the whole `hooks` object.
- **`SettingsFile.cs`** — loads/saves `settings.json` as a raw `JsonNode` DOM (never a typed POCO, so
  unrecognized top-level keys like `env`/`permissions`/`theme` survive round-trips). `Load` returns
  `Ok`/`Missing`/`Empty`/`Malformed`; only `Ok`/`Missing` are `IsWritableForInstall`. `Save` is atomic
  (temp file + `File.Replace`) and takes a one-time `.accel.bak` backup before the first write.
- **`SettingsMerger.cs`** — the core merge engine. Invariants: ownership decided per-entry; a foreign
  matcher group is never modified/reordered/removed; removal prunes empty containers bottom-up
  (`hooks[]` → matcher group → event key → top-level `hooks` object); install is idempotent and rewrites
  only Accel-owned entries **in place at the same array position** when one already exists (preserves the
  matcher group and untouched sibling entries), or appends a new matcher group otherwise.
  `Detect(root, expected)` classifies current state into `NotInstalled`/`PartiallyInstalled`/
  `PortDrift`/`Installed`. `Install`/`Uninstall`/`InstallInto` perform the actual mutation.
- **`StatusLineChain.cs`** — `IStatusLineChainStore` + `StatusLineCapture` (distinguishes "field didn't
  exist" from "not captured yet"). `Install` captures whatever's currently in the field only if it's not
  already Accel-owned and no capture exists yet (never clobbers a real prior capture). `Uninstall`
  restores/removes the field, but only if the field is still Accel-owned at uninstall time (if another
  tool took it over since, it's left untouched).

### 2.3 `Versioning/` — Claude Code version probing and feature gates

- **`ClaudeVersion.cs`** — `major.minor.patch` struct, `TryParse` from `claude --version` output.
- **`ClaudeVersionProbe.cs`** — shells `claude --version`; returns `null` on any failure, never throws.
- **`CurlProbe.cs`** — checks `System32\curl.exe` exists (diagnostic helper; not currently wired into
  `InstallCommand`/`DoctorCommand`).
- **`VersionGate.cs`** — `Feature` enum with hardcoded min-version thresholds (`SubagentStartEvent`,
  `SubagentStatusLineModelAndContextWindow`, `SubagentStatusLineEffort`,
  `ContextWindowCurrentNotCumulative`, `StatusLinePromptId`). `Supports(version, feature)` degrades to
  `false` (most conservative) on unknown version. This directly drives which `AccelHookSpec` entries
  `InstallCommand` requests, and thus what `SettingsMerger.Install` writes into `settings.json`.

### 2.4 `Server/` — the in-process HTTP/WebSocket layer

- **`EventServer.cs`** — ASP.NET Core Minimal API (`WebApplication`), binds to `http://127.0.0.1:{port}`
  only, `builder.Logging.ClearProviders()` so only `EventPrinter` writes to console. Holds four
  instance-lifetime singletons shared across all requests: `SessionState State`, `string[] Roots`
  (loaded once via `RootFoldersConfig.Load()`), `RootsTreeBuilder RootsTree`, `PtyRouteRegistry
  PtySessions`. Routes: `POST /events/{session-start|session-end|subagent-start|subagent-stop}` →
  `HandleEventAsync`; `POST /events/status-line` / `/events/subagent-status-line` → their handlers; then
  delegates to `StateQueryRoutes.Map`, `RootsRoutes.Map`, `RootsTreeRoute.Map`, `PtyRoutes.Map`. Every
  handler always returns HTTP 204 and wraps metrics/printing in a swallow-all try/catch — hook callers
  must never see anything but transport success. `RunAsync`/`BuildApp`+`StartAsync`/`StopAsync` cover
  standalone vs. combined-mode lifecycles.
- **`PtyRoutes.cs`** — `GET /pty/{tabId}` WebSocket upgrade. Defines `IPtyEndpoint` (test seam:
  `ChannelReader<string> Output`, `Write`, `Resize`) and `PtySessionEndpoint` (production adapter over
  `Orchestration.PtySession`). `PtyRouteRegistry` is a `ConcurrentDictionary<string, IPtyEndpoint>`
  explicitly called out as *provisional* — not yet merged with `Orchestration.PtyRegistry`. Security:
  rejects non-WebSocket requests, requires `Origin: https://accel-terminal` (matching the WebView2 virtual
  host), and a uniform 404 for malformed/unknown tabIds. `PumpAsync` runs output-pump and input-pump loops
  concurrently; binary WS frames are raw stdin bytes, text frames are JSON control messages (currently
  only `{"resize":[cols,rows]}`).
- **`RootFoldersConfig.cs` / `RootsRoutes.cs` / `RootsTreeRoute.cs`** — a "root" is a user-configured
  top-level project folder Accel tracks, used to bucket sessions by the transcript's own `cwd`. Config
  supports v1 (flat array, legacy) and v2 (`{roots, sessions}` where `sessions` is a sparse per-session
  override map: displayName/pinned/hidden/lastOpenedUtc). Probed at `%USERPROFILE%\.claude\accel-folders.json`
  → `<exeDir>\folder.json` → `<cwd>\folder.json` (first found wins, no fallthrough on parse failure).
  `GET /roots` returns the roots loaded once at startup; `GET /roots/tree` calls
  `Metrics.RootsTreeBuilder.Build(...)` fresh each request (including a fresh re-read of the sessions
  override map).
- **`StateQueryRoutes.cs`** — read-only GETs over `SessionState`: `/sessions`, `/sessions/{id}`, `/agents`,
  `/state`, using DTOs with explicit snake_case `JsonPropertyName` wire fields.
- **`RawPayloadCapture.cs`** — backs `accel run --dump-raw <dir>`; writes each raw hook request body to a
  uniquely named file, best-effort, never affects the 204 response.
- **`EventPrinter.cs`** — static console logger for lifecycle events only (session-start/end,
  subagent-start/stop); status-line events are deliberately not printed (they fire every UI tick).

### 2.5 `Metrics/` — transcript/hook payload parsing into shared in-memory state

- **`MetricsPipeline.cs`** — static, non-throwing entry points called directly from `EventServer`:
  `HandleSubagentStop` (reads the sub-agent's transcript tail + sibling `.meta.json` via
  `TranscriptReader`/`MetaJsonReader`, resolves context window via `ModelWindowTable`, calls
  `SessionState.UpdateAgentRecord` then `MarkAgentEnded`), `HandleStatusLine` (parses the main-session
  status-line payload into a `SessionSnapshot`, calls `UpdateSessionSnapshot`), `HandleSubagentStatusLine`
  (upserts a Live `AgentRecord` per `tasks[]` entry — never resurrects an already-`Ended` agent — then
  calls `ReconcileLiveAgents` to flip any agent missing from this tick to `Stale`).
- **`SessionState.cs`** — the in-memory, non-persisted, thread-safe store: two `ConcurrentDictionary`s
  (`sessionId → SessionSnapshot`, `agentId → AgentRecord`). Every mutator raises `event Action? Changed` —
  **this is the primary push signal**; the WPF monitor subscribes in-process (no HTTP, no polling) and
  marshals to its own UI thread.
- **`RootsTreeBuilder.cs`** — inputs: configured roots, `SessionState`, the Claude projects directory
  (`%USERPROFILE%\.claude\projects` by default), and the roots-config session overrides. Enumerates every
  session `.jsonl` transcript, attributes each to a root by the transcript's own recorded `cwd`
  (segment-boundary-safe prefix match), merges in live `SessionState` data when present (falls back to
  tailing the transcript otherwise), and attaches live sub-agents grouped by `ParentSessionId`. Maintains
  three long-lived caches (`_headCache` for immutable cwd/first-timestamp lookups, `_tailCache` keyed by
  file length + `LastWriteTimeUtc`, `_agentStartCache` keyed by `agent_id` for tier-1 agent start
  resolution — see below) so it's cheap to call on every hook tick. Never throws; degrades to fewer
  results. A single `DateTime.UtcNow` is captured once per `Build()` call and reused for `GeneratedAtUtc`
  and every row's `DurationMs`, so no two rows in the same document are measured against different clocks.
  Output: `RootsTreeDto{Roots[], UnattributedSessions, UnattributedAgents, GeneratedAtUtc, ScanMs}`, where
  each `SessionTreeDto`/`AgentTreeDto` additionally carries:
  - `StartedAtUtc` / `StartedAtSource` — a session's start time is derived every call from the permanent
    head cache's `FirstTimestampUtc` (the transcript's own first parseable `timestamp` field, scanning
    forward past any mode-marker first line — see `TranscriptHeadReader`), tagged `"transcript"`, or
    `null`/`null` if no timestamp was found. An agent's start time comes from a **three-tier tolerant
    ladder**, applied in `ToAgentDto`/`ResolveAgentStartedAt`: **tier 1** — the head-window timestamp of
    the agent's own transcript, resolved via `record.TranscriptPath` (set by `HandleSubagentStop`) when
    present, otherwise a convention path
    `<projectsDir>\<ProjectDir>\<SessionId>\subagents\agent-<AgentId>.jsonl` derived from the owning
    session — cached permanently on hit and retried at most once per 10 seconds on miss (via
    `_agentStartCache`, keyed by `agent_id` so a later path-derivation change never needs to invalidate
    it), tagged `"transcript"`; **tier 2** — a `subagentStatusLine` task's own `startTime` field, parsed
    in `MetricsPipeline.HandleSubagentStatusLine`, tagged `"task_start_time"`; **tier 3** — the earliest
    `ReceivedAtUtc` ever observed for that `agent_id`, applied as a fallback inside
    `SessionState.UpdateAgentRecord`'s merge, tagged `"first_seen"`. Tiers 2/3 are resolved once, when the
    `AgentRecord` is written; tier 1 is resolved fresh on every `RootsTreeBuilder.Build()` call and always
    takes precedence when it hits.
  - `DurationMs` — `null` if `StartedAtUtc` is null, else `Math.Max(0, (end - StartedAtUtc).TotalMilliseconds)`
    where `end` is `nowUtc` for a live row or `LastActivityUtc`/`AsOf` for a historical/ended one (the
    `Math.Max(0, …)` clamps a clock-skew case rather than rendering a negative duration).
  - `ConsumedTokens` — for a session, `UsedTokens` (input + cache, **no output tokens** — the `statusLine`
    payload has no output-token field), always paired with `ConsumedTokensIsContextOnly = true` so the UI
    can render that caveat; for an agent, `InputTokens + OutputTokens + CacheCreationInputTokens +
    CacheReadInputTokens` (a genuine total, since an agent record does carry an output-token count).
- **`TranscriptReader.cs` / `TranscriptHeadReader.cs` / `MetaJsonReader.cs`** — tail/head readers over
  session `.jsonl` transcripts and their sibling `.meta.json` files; extract model id, token usage,
  effort level, cwd, and derived display labels.
- **`ModelBadgeTable.cs` / `ModelWindowTable.cs` / `EffortBarLevel.cs` / `ModelEffortTable.cs`** — pure,
  static, side-effect-free lookup tables shared between backend metrics computation and UI rendering
  (also reused directly by `Cli/MonitorTreeBuilder.cs` and UI controls): model id → badge letter/color,
  model id → context-window token size (with an "assumed, not matched" flag propagated through the
  DTOs), effort level string → 0–5 bar level (five tiers: low/medium/high/xhigh/max), and
  `ModelEffortTable.SupportsEffort(...)` — whether a model family/badge recognizes the effort knob at
  all (Haiku does not; an unrecognized family/badge degrades to "supports effort" rather than hiding the
  control on an unmatched model).

### 2.6 `Orchestration/` — PTY/process spawn, tracking, and teardown

This is the most complex layer: it spawns Windows ConPTY-backed `claude` child processes, tracks them,
and guarantees they don't outlive the app even across crashes.

- **`IPtySessionHost`** — a 3-member interface (`SessionEnded` event, `TabIds()`,
  `CloseAsync(tabId, ct)`, `TryGetProcessId(tabId)`) implemented by `PtyRegistry`. Exists so
  `App/ViewModels/TabsViewModel.cs` can be unit-tested against a fake without a real ConPTY/process, and
  so a tab view model structurally cannot obtain a `PtySession` reference or call `PtySession.Dispose()`
  directly — closing always routes through `CloseAsync`.
- **`ConPty.cs`** — raw Win32 ConPTY interop: `CreatePipe`/`CreatePseudoConsole`/`ResizePseudoConsole`/
  `ClosePseudoConsole` plus `CreateProcessW` via `STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`.
  `ConPtySession.Start(spec)` supports `CreateSuspended` so the caller can assign the child to a Job
  Object before any instruction runs. Hands out raw, unbuffered `FileStream`s over the pipes — no
  decoding/pumping here (that's `PtySession`'s job). `Dispose()` order is load-bearing: close stdin write
  end (EOF to child) → `ClosePseudoConsole` (can block draining) → close remaining handles.
- **`PtySession.cs`** — one launched child process + its ConPTY + output pump. `PtyLaunchSpec` builds a
  real argv array (never a shell string) and refuses `.cmd`/`.bat`/`.ps1` shims outright (ConPTY-attaching
  a shim isn't supported). `PtySession.Start` launch order (spawn suspended → `AccelJobObject.AssignProcess`
  while still suspended → open an independent `Process` observer + capture `ProcessStartTimeUtc` →
  start the output pump → `ResumeMainThread()`) closes the race window where a child could escape job
  assignment or spawn grandchildren before being tracked. Output is exposed via a bounded
  `Channel<string>` fed by a dedicated background thread (`PtyOutputPump`) doing blocking pipe reads with
  one persistent `Decoder` (so multi-byte UTF-8 split across reads decodes correctly); real backpressure —
  if the consumer stops reading, the pump blocks, which blocks conhost, which blocks the child.
  `ExitReason` (`ChildExited` vs `TornDown`) is resolved by polling for exit *before* marking teardown
  requested, to correctly classify a child that exited on its own moments before `Dispose()`.
- **`PtyRegistry.cs`** — the app-lifetime `tabId → PtySession` map (`ConcurrentDictionary`), sole owner of
  `PtySession.Dispose()`. `Shared` is a lazy process-lifetime singleton; the constructor is also public for
  scoped/test instances. An interlocked per-entry gate (`Entry.TryBeginClose`) ensures exactly one caller
  drives teardown for a given tab; removal from the live map happens *before* `PtySession.Dispose()` runs.
  Close escalation: verify exit via an independent `IPtyProcessObserver` within `CloseTimeout` (5s), else
  `Process.Kill(entireProcessTree: true)` and wait `ForceKillGrace` (2s) — outcomes reported as data
  (`Closed`/`ForceKilled`/`ForceKillFailed`/`ExitUnverified`), never thrown. `CloseAllAsync`/`Dispose()`
  drive app-exit teardown of every tracked session.
- **`AccelJobObject.cs`** — one process-wide, unnamed Windows Job Object with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` — closing the job handle kills every process still assigned to it.
  `Shared` is a `Lazy<AccelJobObject>` deliberately never disposed by app code (the OS closes it, killing
  survivors, at real process exit) — this is the **primary orphan backstop**. Children are assigned while
  still suspended so nothing can escape the net.
- **`PtyPidRegistry.cs`** — an on-disk JSON ledger (`%USERPROFILE%\.claude\accel-sessions.json`) of
  `(SessionId, Pid, ProcessStartTimeUtc, Cwd, LaunchedAtUtc)`, distinct from the in-memory `PtyRegistry`.
  Exists to survive Accel process restarts/crashes so a later run can detect leftover `claude` processes
  that escaped the Job Object backstop. `Reconcile(...)` is a pure, PID-reuse-safe staleness check (an
  entry is stale if the PID is dead, or alive but its start time doesn't match within 2s tolerance).
- **`PtyOrphanReconciler.cs`** — runs at Accel startup: loads `PtyPidRegistry`, classifies entries as
  `Adoptable` (real, identity-confirmed leftover process) vs `Stale` (dead/PID-reused junk, deleted from
  the file). Default policy is "report, never touch" — adoptable orphans are left running and in the
  file; killing user work silently at startup is treated as hostile. Exposes `KillOrphan`/
  `AdoptAsDetached` primitives for a future UI to call; no UI wires them up yet.
- **`PtyShutdownCoordinator.cs`** — wires `PtyRegistry.CloseAllAsync` to all of Windows' app-exit paths:
  explicit `Dispose()`/`Shutdown()`, a real native `SetConsoleCtrlHandler` (catches `CTRL_CLOSE_EVENT`,
  which `Console.CancelKeyPress` alone misses), and `AppDomain.ProcessExit` as a last-chance catch-all.
  All three converge on one interlocked `Shutdown(trigger)` call with per-path timeout budgets; on
  timeout it cancels the inner token to bring `PtyRegistry`'s force-kill forward, and whatever remains
  falls to `AccelJobObject`'s kill-on-close at real process death.
- **`ClaudeSessionStatusFile.cs`** — reads (never writes) `~/.claude/sessions/<pid>.json`
  (`sessionId`/`name`/`status`/`updatedAt`), written by Claude Code itself. Used by `SlashCommandDriver`
  and `TabsViewModel`'s focused-tab polling to detect session-id drift (e.g. `/clear`) and to gate slash
  command injection on the session being idle.
- **`ClaudeCliLocator.cs`** — walks `PATH`, preferring a native `claude.exe` over a `.cmd`/`.bat`/`.ps1`
  shim; never caches, since Claude Code self-updates in place.
- **`SessionRemover.cs` / `SessionRemoverExecutor.cs`** — plan/execute split for deleting a session's
  on-disk data. `SessionRemover.Plan` (pure/read-only) builds a validated `SessionRemovalPlan` (transcript
  always deleted last); every candidate is checked to resolve strictly inside `%USERPROFILE%\.claude`,
  have an exact-GUID leaf name, and have no symlink/junction anywhere on its path. `SessionRemoverExecutor
  .Execute` is the only class allowed to delete: re-validates every target immediately before deleting
  (never trusts a stale plan), re-checks a live-session predicate before every delete and before rewriting
  the shared `history.jsonl`, and aborts remaining steps on the first failure.
- **`SlashCommandDriver.cs`** — drives a live session's slash commands by writing sanitized text to PTY
  stdin (`SlashCommandInputSanitizer` allowlists safe characters, rejects control chars/line separators)
  and polling `ClaudeSessionStatusFile` for a caller-supplied completion predicate — never by
  screen-scraping terminal output.

### 2.7 `App/` — WPF UI layer

- **Composition** — `App.xaml` is compiled as `Page` (not `ApplicationDefinition`) so it doesn't generate
  a second `Main` colliding with `Program.cs`'s top-level statements; `Program.cs` is the true composition
  root. `MainWindow` uses no DI container — chained constructor overloads with nullable params. Each panel
  is bound individually (`PanelA.DataContext`, `PanelC.DataContext`, ...) — never `Window.DataContext` —
  so panels can't accidentally cross-bind.
- **`TabsViewModel.cs`** (panel C) — owns `ObservableCollection<TabViewModel> Tabs`/`SelectedTab`; a pure
  projection over `IPtySessionHost` (never holds a `PtySession`). The single writer of the focused session
  id app-wide via `ISessionSelectionWriter` obtained once from `SessionSelectionService.AcquireWriter()`
  (a second call throws). Polls the focused tab's `ClaudeSessionStatusFile` (default 1s) to catch
  in-session `/clear`/`/compact` session-id drift.
- **`RootsPanelViewModel.cs`** (panel A) — owns the folder/session/agent tree, fed exclusively by
  `ITelemetryFeed` (never touches `SessionState.Changed`, a `FileSystemWatcher`, or HTTP directly).
  Rebuild-and-diff: captures expanded keys/selection before clearing, re-expands via
  `Cli.MonitorTreeExpansion`. Read-only consumer of `ISessionSelectionService` (sets `IsFocused`, never
  writes selection).
- **`ITelemetryFeed`** — production stack: `EventServerTelemetrySource` wraps the in-process `EventServer`
  and calls the *same* `RootsTreeBuilder.Build` the Kestrel `/roots/tree` route calls (guaranteeing the UI
  and the HTTP route render byte-identical trees, with zero HTTP round-trip for the UI). `TelemetryFeed`
  composes that with a `FileSystemWatcher` on the projects directory and the reused `Cli.DebounceCoalescer`
  (250ms), all marshaled onto the UI thread via `IUiThreadDispatcher` before hitting the coalescer.
- **`ISessionSelectionService` / `ISessionSelectionWriter`** — app-wide "focused session" hub backed by a
  private `WeakReferenceMessenger` (never the shared `.Default`). The read interface has no mutator; only
  the single writer obtained via `AcquireWriter()` can change focus — enforced structurally, not by
  convention.
- **`IUiThreadDispatcher` / `IDebounceTimer`** — seams over `Dispatcher`/`DispatcherTimer` purely for
  headless testability, mirroring the pattern used by the legacy WinForms monitor this UI replaced.
- **Dialogs** (`CreateSessionDialog`, `EditSessionArgsDialog`, `RenameSessionDialog`) — each pairs a
  WPF-agnostic ViewModel (`[RelayCommand] Confirm`/`Cancel`, `event RequestClose`) with a thin
  code-behind. `CreateSessionDialogViewModel` builds a `PtyLaunchSpec` via `PtySession.CreateClaudeSpec`
  and starts it via `PtySession.Start`; the generated session GUID is used as both `--session-id` and the
  tab id, load-bearing for keying panel A's tree to the same id. `EditSessionArgsDialogViewModel` stores
  edited resume args in `SessionResumeArgsStore`, applied on the session's *next* resume. `RenameSessionDialog`
  validates via the same `SlashCommandInputSanitizer` the PTY write path uses.
- **`TerminalView.xaml.cs`** — hosts one WebView2 running vendored xterm.js + FitAddon (`wwwroot/xterm/`,
  mapped to virtual host `accel-terminal`), using a dedicated WebView2 user-data folder under
  `%USERPROFILE%\.claude\accel-webview2\`. There is deliberately **one `TerminalView` for the whole app**,
  reattached per tab selection (`AttachPtyAsync(tabId, port)` calls `window.accelAttachPty` which opens a
  `ws://…/pty/{tabId}` connection) rather than one WebView2 instance per tab, trading scrollback-on-switch
  for far lower resource cost.
- **`EffortBarsControl`** — radial ring gauge rendering `Metrics.EffortBarLevel`'s 0–4 scale (arc for 1–3,
  filled disc for max, shape as well as color for accessibility).
- **`AgentGraphViewModel.cs`** (panel E, Phase 6) — a *second* reader on the same `ITelemetryFeed` instance
  and the same read-only `ISessionSelectionService` panel A uses, never a filtered view of
  `RootsPanelViewModel`'s own tree (panel A's node objects are rebuilt wholesale on every telemetry tick,
  so a reference into them would be stale ~250ms later). `Rebuild` calls `Cli.MonitorTreeBuilder.Build`
  itself (its only public entry point) and selects the focused session's `MonitorSessionNode` out of the
  result; the focused session's live sub-agents become one `AgentGraphNodeViewModel` per node
  (`App/ViewModels/AgentGraphNodeViewModel.cs`), parent first. `AgentGraphControl` (`App/Controls/`, next to
  `EffortBarsControl`) renders them via a `DataTemplate`-per-card `ItemsControl` over a `Canvas`, with bezier
  connectors built in code-behind from the pure, WPF-free `AgentGraphLayout.Compute` (horizontal,
  left-to-right, column-major layout math, unit-tested without any UI thread).
- **`FilesPanelViewModel.cs`** (panel B, top) — a read-only file/folder tree rooted at the focused
  session's cwd (falling back to panel A's own tree selection when no session is focused, via a
  `RootsPanelViewModel` reference), another independent reader of the same `ITelemetryFeed`/
  `ISessionSelectionService` pair panel A and E use. `FilesPanelNodeViewModel` children load lazily on
  first expand (`FilesTreeBuilder.BuildChildren`, one level at a time) rather than eagerly for the whole
  subtree — an earlier eager/shared-budget walk could silently truncate a top-level listing when an
  earlier sibling's subtree was large. Raises `FolderExpanded`/`FolderCollapsed` so `GitPanelViewModel`
  can follow which folder is being drilled into. Expand/collapse only — no file-open, no stage/commit.
- **`GitPanelViewModel.cs`** (panel B, bottom) — a flat `git status` list (via `GitStatusBuilder.Build`)
  for the same focused root `FilesPanelViewModel` resolves, split into `StagedChanges`/`Changes`
  (unstaged + untracked), VS Code Source Control-style. Wired to `FilesPanelViewModel.FolderExpanded`/
  `FolderCollapsed` in `Program.cs` so drilling into a repo folder in the file tree switches this section
  to that repo. List-only — no stage/unstage/discard/commit action yet.
- **`McpSkillsPanelViewModel.cs`** (panel A, bottom third) — the focused session's MCP-tool and Skill
  hit counts as two flat lists (`ToolUsageRowViewModel`), most-used first. A third independent reader of
  the same `ITelemetryFeed`/`ISessionSelectionService` pair; all its data already rides on the pushed
  `SessionTreeDto` (`McpUsage`/`SkillUsage`, populated by `RootsTreeBuilder`), so a rebuild is a lookup
  plus clear-and-repopulate, no I/O of its own. Historical (not-currently-running) sessions report empty
  usage arrays — Accel only counts `PostToolUse` hits observed while it was running.
- **Remaining `Services/`**: `CommonCliFlags` (permission-mode enum → `--permission-mode` argv),
  `ExtraArgsParser` (tokenizes free-text into a real argv array, quote-aware), `IFolderPickerService`/
  `IUserConfirmationService` (testable wrappers over WinForms folder picker / MessageBox),
  `RootFolderEditor` (pure add/remove logic over `accel-folders.json`, never touches the filesystem
  itself), `SessionResumeArgsStore` (in-memory, per-session pending resume args).

## 3. Key Data Flows

### 3.1 Hook event → UI-ready state

1. A Claude Code hook fires → `accel.exe notify` (self-invoked, see `Cli/NotifyCommand.cs`) POSTs
   JSON to `http://127.0.0.1:<port>/events/<name>` (or
   `/events/status-line` / `/events/subagent-status-line`), per the entries `Settings.SettingsMerger`
   installed into `settings.json`.
2. `Server/EventServer.cs`'s mapped route reads the body, optionally writes it via
   `RawPayloadCapture.TryWrite`, prints lifecycle events via `EventPrinter`, and always returns 204.
3. Depending on event: `Metrics.MetricsPipeline.HandleSubagentStop` / `.HandleStatusLine` /
   `.HandleSubagentStatusLine` parses the payload (using `TranscriptReader`/`MetaJsonReader` for
   sub-agent transcript tails) and mutates the shared `SessionState` (`UpdateSessionSnapshot`,
   `UpdateAgentRecord`, `MarkAgentEnded`, `ReconcileLiveAgents`); `SessionEnd` calls `state.MarkSessionEnded`
   directly from `EventServer`.
4. Every `SessionState` mutator raises `SessionState.Changed`. In the combined WPF process, this fires
   `App.Services.EventServerTelemetrySource`'s `Changed` event → `TelemetryFeed` (debounced via
   `Cli.DebounceCoalescer`) → `TelemetryFeed.Publish()` calls `RootsTreeBuilder.Build(...)` in-process →
   `SnapshotAvailable` → `RootsPanelViewModel.Rebuild` → `Cli.MonitorTreeBuilder.Build` →
   `ObservableCollection<RootsPanelNodeViewModel>` → WPF `TreeView` binding.
5. Independently, `GET /roots/tree` (`Server/RootsTreeRoute.cs`) calls the *same*
   `RootsTreeBuilder.Build(...)` for any external/API consumer, and `GET /sessions`, `/agents`,
   `/sessions/{id}`, `/state` (`Server/StateQueryRoutes.cs`) expose the same `SessionState` read-only as
   flat DTOs.

### 3.2 PTY session lifecycle

**Spawn** (e.g. "Create session" in the UI): `CreateSessionDialogViewModel` → `PtySession.CreateClaudeSpec`
(resolves `claude` via `ClaudeCliLocator`, strips nested-session env markers, builds + validates a
`PtyLaunchSpec`) → `PtySession.Start`: spawn suspended via `ConPtySession.Start` → `AccelJobObject
.AssignProcess` while still suspended → open an independent `Process` observer + capture
`ProcessStartTimeUtc` (both provably tied to this exact process pre-resume) → start the `PtyOutputPump`
background thread → `ResumeMainThread()`. The caller then calls `PtyRegistry.Register(tabId, session)`
(arms an exit continuation) and persists a `PtyPidRegistry.Add(...)` entry to `accel-sessions.json`, and
`MainWindow` adds a tab in `TabsViewModel` keyed by the same session GUID.

**Normal shutdown**: `PtyRegistry.CloseAsync(tabId)` (or, at app exit, `PtyShutdownCoordinator.Shutdown()`
→ `PtyRegistry.CloseAllAsync`) → interlocked per-entry gate wins → entry removed from the live map →
`PtySession.Dispose()` (poll-for-exit first to correctly classify self-exits, cancel the pump, close the
ConPTY, join the pump thread) → `PtyRegistry` verifies exit via its own `IPtyProcessObserver` within
`CloseTimeout`, escalating to `Process.Kill(entireProcessTree: true)` if needed → `SessionEnded` fires →
caller removes the `PtyPidRegistry` entry.

**Crash/orphan path**: if Accel itself dies without running teardown, the OS closing
`AccelJobObject.Shared`'s handle kills every still-assigned `claude.exe` (primary backstop). Any survivor
(e.g. job assignment failed or the child broke away) shows up on the *next* Accel startup as a leftover
`accel-sessions.json` entry; `PtyOrphanReconciler.ReconcileAtStartup` classifies it as `Adoptable`
(left running, reported) or `Stale` (removed from the ledger) — no automatic killing.

## 4. Key Abstractions Worth Knowing

- **`Orchestration.IPtySessionHost`** — the seam between the UI's `TabsViewModel` and the real PTY
  process layer (`PtyRegistry`). Exists for testability and to structurally prevent UI code from calling
  `PtySession.Dispose()` directly; all teardown goes through `CloseAsync`.
- **`App.Services.ITelemetryFeed`** / **`ITelemetrySource`** — the seam between backend state
  (`EventServer`/`SessionState`/`RootsTreeBuilder`) and the UI. `EventServerTelemetrySource` is the
  production bridge that calls the same tree-building code the HTTP route uses, guaranteeing UI/API
  parity without an HTTP round-trip.
- **`App.Services.ISessionSelectionService` / `ISessionSelectionWriter`** — single-writer, many-reader
  seam for "which tab is focused," enforced via a one-shot `AcquireWriter()` rather than convention.
- **`Server.PtyRoutes.IPtyEndpoint`** — test seam over the WebSocket-to-PTY bridge (`PtySessionEndpoint`
  is the production adapter over `Orchestration.PtySession`); `PtyRouteRegistry` is explicitly a
  provisional registry, separate from and not yet merged with `Orchestration.PtyRegistry`.
- **`Settings.IStatusLineChainStore`** — persistence seam for "what command did we replace," needed
  because install and statusline-invocation are separate process lifetimes.
- **`App.Services.IUiThreadDispatcher` / `IDebounceTimer`** — testability seams over WPF's `Dispatcher`
  and timers, reused by `TelemetryFeed`/`RootsPanelViewModel`/`TabsViewModel`.

## 5. Notable Design Decisions With Architectural Implications

- **One process, no IPC.** Install, HTTP server, and UI run in the same process with direct object
  references (`EventServer.State`, `EventServer.RootsTree`) rather than as separate processes
  communicating over HTTP/pipes. This is why `EventServerTelemetrySource` can call `RootsTreeBuilder
  .Build` directly instead of making a loopback HTTP request — but it also means the UI and the HTTP API
  share failure domains (a UI-thread deadlock affects hook responses only in that Kestrel runs on its own
  thread pool, unaffected by the UI thread).
- **Push-based refresh, not polling**, for the primary UI data path: `SessionState.Changed` →
  `ITelemetryFeed` → debounced rebuild. The only polling in the system is narrow and intentional:
  `TabsViewModel`'s 1s focused-tab status-file check (to catch `/clear`/`/compact` session-id drift that
  produces no hook event) and `SlashCommandDriver`'s completion-predicate poll (because slash-command
  results have no event either).
- **Windows Job Objects, not tree-walking, for orphan cleanup.** `AccelJobObject` with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` is the primary defense against leaked `claude` processes; the
  on-disk `PtyPidRegistry` + `PtyOrphanReconciler` is a secondary, conservative ("report, never kill
  automatically") backstop for the rare case a process escapes the job.
- **Ownership-marker-based settings merge**, not whole-object ownership. Every hook entry and every
  status-line field independently proves Accel ownership (a header marker, or a command-string token
  match) before `SettingsMerger` will touch it, so hand-edited or third-party hooks/fields sitting
  alongside Accel's in the same `settings.json` are never disturbed.
- **Plan/execute split for destructive session deletion** (`SessionRemover` / `SessionRemoverExecutor`):
  planning is pure and re-validated at execute time rather than trusted, specifically to defend against
  TOCTOU issues (symlink/junction swaps, a session going live mid-delete) — the plan can go stale between
  being computed and being executed.
- **Exec-form-only hook commands.** `HookEntry` never uses shell form for the self-invoked
  `notify` event hooks, eliminating an entire class of quoting/injection bugs across
  `cmd.exe`/PowerShell/sh at the cost of needing a separate `ShellCommandRunner` specifically for
  chaining the pre-existing `statusLine` field (which Claude Code only supports as a shell string).
- **Single shared `TerminalView`/WebView2 instance**, reattached per tab, rather than one per tab — an
  explicit resource-vs-scrollback tradeoff (each WebView2 instance costs multiple OS processes + GPU
  surface).
- **`net8.0-windows` + WPF + WinForms + Kestrel in one executable**, self-contained single-file publish.
  `InvariantGlobalization` is explicitly disabled (see `accel.csproj` comment) because WPF's text input
  pipeline needs real culture resolution for non-English keyboard layouts — a constraint that will bite
  any future attempt to shrink the publish size via invariant mode.

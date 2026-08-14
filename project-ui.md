# Glaude UI — Minimalistic Local Monitor Window

## Purpose

A small, non-interactive Windows desktop window that gives an at-a-glance, auto-refreshing
view of:
- the configured **root folders** Glaude scans (v1: read from `folder.json`),
- for each root folder, the **Claude Code sessions** that exist under it (both currently
  running and historical), with model/effort/context, running ones visually distinguished,
- for each **running** session, its **running sub-agents**, same fields, as a nested list.

"Non-interactive" = read-only display, no buttons/actions, no way to trigger install/
uninstall/etc from this window — it is purely a viewer. The window itself can still be
closed normally (standard title-bar close button); "non-interactive" describes the content,
not the window chrome.

This is a new capability, not in the original `project.md`/`project-plan.md` scope (those
cover the server/CLI). It builds on top of the already-shipped Phase 3d state-query routes
(`GET /sessions`, `/agents`, `/state`) and Phase 3b-ii's transcript-tailing components
(`TranscriptReader`, `ModelWindowTable`), but needs **new** capability those don't have:
listing sessions that exist on disk regardless of whether they are currently live.

Evidence tiers are the ones defined in `project.md` → "Model/Effort/Context metrics sourcing"
(**[VERIFIED-DISK]** / **[DOC]** / **[ASSUMED]** / **[EXPERIMENT]**, plus **[VERIFIED-LIVE]**
for a completed capture). This document adds one sibling of the latter, **[VERIFIED-BUILD]** —
a claim proved by actually compiling/publishing a throwaway project on this machine
(SDK 8.0.424, pinned by `global.json`).

## Decisions made in audit (2026-08-13)

All five items previously framed as open questions are now **decided**. Summary of what
changed and why; the body of the document below reflects the decisions, not the tradeoffs.

| # | Was open | Decision | Why |
|---|---|---|---|
| 0 | `folder.json` location | Probe `%USERPROFILE%\.claude\glaude-folders.json`, then `<exe dir>\folder.json`, then `<cwd>\folder.json`; first that parses wins | Works both for the repo-root dev/testing file the request asked for **and** for a published single-file `Glaude.exe` with no repo next to it. No either/or needed |
| 1 | Verb wiring direction | **Neither (a) nor (b): one project, one exe.** Change the *existing* `Glaude.csproj` to `net8.0-windows` + `UseWindowsForms=true`, keep `Microsoft.NET.Sdk.Web`, add a `ui` verb to the existing `ArgParser`/`Program.cs` switch. No `Glaude.Ui.csproj`, no `Process.Start`, no reference-direction problem | The doc's premise ("mixing `UseWindowsForms` into an ASP.NET Core SDK project … risks tooling issues") is **wrong** — measured, see "Stack decision". Two exes would break Phase 8's single-file promise and cost ~65 MB more on disk |
| 2 | Historical session naming | **Derive a label from the first real user message** (head-read of the transcript), truncated, display-only, with a short-`session_id` fallback | Same read-only, tolerant, display-only posture as the rest of Glaude, which already reads whole transcripts for metrics. The same head-read is needed anyway for `cwd` (decision 3), so it costs nothing extra |
| 3 | Tree shape / slug collision | **Exactly 3 levels**; a session belongs to exactly one root; attribution is by the transcript's **own `cwd` field**, not by decoding the slug. Ties (nested configured roots) go to the **longest** matching root | Turns the slug collision from a documented limitation into an actually-fixed problem, and removes reliance on an unverified encoding rule. See "Root attribution" |
| 4 | Packaging | Self-contained single-file, **plus `IncludeNativeLibrariesForSelfExtract=true`**, no trimming | Mirrors Phase 8. The extra property is not optional: without it WinForms/WPF native DLLs land *beside* the exe and "single executable" silently stops being true — measured |

Corrections and gaps found by this audit, beyond the five decisions:
- **Correction:** the claim that a live session's name is already captured by Glaude is
  **false** — nothing reads `session_name`. See "Completeness gaps", GAP A.
- **Correction:** live sub-agents currently have a **null parent session id**, so they cannot
  be nested under their session at all. See GAP B. This is the direct analogue of
  `project-plan.md`'s Phase 3d gap: the data arrives in a payload and nothing captures it.
- **Correction:** `TranscriptReader` is a *tail*-only reader (last 64 KB) **[VERIFIED-DISK]**,
  so it cannot supply a first-message label or a `cwd`. A new head-read helper is needed.
- **Correction:** the tree shape is 3 levels, not "three or four".
- **Added:** a fully specified `GET /roots/tree` body (was explicitly left as "an
  implementation detail for the plan" — not enough for a plan writer to implement without
  guessing).
- **Added:** a re-read/caching rule for the per-tick disk scan, which as originally written
  would re-parse every transcript on this machine every 2 s.

## Stack decision

**Windows Forms** (.NET 8, `net8.0-windows`, `UseWindowsForms=true`), not WPF/Avalonia/MAUI.
Rationale: this UI is exactly "one read-only hierarchical tree, auto-refreshed" — WinForms'
`TreeView` control is a direct, zero-ceremony fit (no XAML, no data-binding/MVVM machinery
needed for a non-interactive view), the smallest addition to a codebase that is otherwise a
console/ASP.NET-minimal-API app, and it stays consistent with "most obvious technology for
Windows-only, minimal effort" given the whole project already targets `win-x64` only.

### No new project — the existing `Glaude.csproj` grows a WinForms TFM

What `src/Glaude/Glaude.csproj` actually contains today **[VERIFIED-DISK]**:
`Sdk="Microsoft.NET.Sdk.Web"`, `OutputType=Exe`, `TargetFramework=net8.0`,
`RuntimeIdentifier=win-x64`, `PublishSingleFile=true`, `SelfContained=true`,
`InvariantGlobalization=true`, `ImplicitUsings`, `Nullable`. So the doc's original factual
claim (plain `net8.0` + `Microsoft.NET.Sdk.Web`) was **correct**.

The *risk* claim attached to it was **not**. Measured on this machine **[VERIFIED-BUILD]**: a
`Microsoft.NET.Sdk.Web` project with `TargetFramework=net8.0-windows`, `UseWindowsForms=true`,
`OutputType=Exe`, `RuntimeIdentifier=win-x64`, `SelfContained`, `PublishSingleFile` —
i.e. exactly today's `Glaude.csproj` plus two lines — **builds and publishes with 0 warnings
and 0 errors**, and a single exe built that way runs both a minimal-API `WebApplication` and
`ApplicationConfiguration.Initialize()` + `System.Windows.Forms.Form` in the same process.
There is no tooling hazard to avoid here; both `Microsoft.AspNetCore.App` and
`Microsoft.WindowsDesktop.App` are shared frameworks and a self-contained publish simply
bundles both.

**Decision: modify `src/Glaude/Glaude.csproj` in place** —
`<TargetFramework>net8.0-windows</TargetFramework>`, `<UseWindowsForms>true</UseWindowsForms>`,
`<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>`. Do **not**
add `src/Glaude.Ui/Glaude.Ui.csproj`.

Concrete, known costs of this decision (all small, all must be in the execution plan):
- `tests/Glaude.Tests/Glaude.Tests.csproj` targets `net8.0` and project-references
  `Glaude.csproj` **[VERIFIED-DISK]**. A `net8.0` project cannot reference a `net8.0-windows`
  one, so the test project's TFM must move to `net8.0-windows` in the same change. One line;
  the tests are already Windows-only in practice (they exercise `settings.json` paths).
- `publish.ps1` hardcodes `src\Glaude\bin\Release\net8.0\win-x64\publish` **[VERIFIED-DISK]** —
  becomes `net8.0-windows`. One line.
- `OutputType` stays `Exe` (console subsystem), **not** `WinExe`, because the same binary is
  still the CLI. Consequence: `glaude ui` shows the console window *and* the form. **Do not**
  try to hide it (`FreeConsole`/`ShowWindow` on the attached console would hide the user's own
  terminal when `glaude ui` is typed into one). Accept it, and print one line
  ("Glaude UI attached to http://127.0.0.1:{port} — close the window to exit") so the console
  is informative rather than looking like a bug.
- Size: see "Packaging".

**Process model: a separate process from `glaude run`, connecting over loopback HTTP** — same
architecture as the existing `glaude sessions --watch` CLI verb, not an in-process window
hosted inside the server. Rationale: the server may already be running standalone (as it is
right now on this machine); the UI should attach to it the same way the CLI does, not compete
for the same process/port. Note this is a *process* boundary, not a *project* boundary — one
`Glaude.exe` that can be run twice, as `glaude run` and as `glaude ui`.

### Verb wiring (decision 1, resolved)

`glaude ui [--port <n>]` is an ordinary verb in the machinery that already exists
**[VERIFIED-DISK]**:
- `Cli/ArgParser.cs`: add `Ui` to the `Verb` enum and `"ui" => (Verb.Ui, null)` to
  `ParseVerb`'s switch. `--port` is already parsed globally for every verb, so no new flag
  handling is needed at all.
- `Program.cs`: add a `case Verb.Ui:` to the existing hand-rolled switch, next to
  `case Verb.Sessions:`, and extend the usage string in the `default:` arm.
- The verb body reuses `Cli/GlaudeStateClient.cs` directly (same process, same assembly —
  no project reference in either direction). `GlaudeStateClient` gains the two new calls
  (`GetRootsAsync`, `GetRootsTreeAsync`) alongside `GetStateAsync`/`GetSessionAsync`, following
  the existing "own nullable POCOs mirroring the server DTOs" convention documented in that
  file's header comment.

Why not the two options the doc originally listed:
- **(a) `Process.Start` a sibling `Glaude.Ui.exe`** — invents a failure mode that single-file
  distribution is specifically supposed to prevent. Phase 8 ships one file that users copy
  anywhere; a second exe that must be adjacent means `glaude ui` can fail with "companion
  executable not found" on a perfectly valid deployment. It also roughly doubles the payload
  (two self-contained runtimes: 91.6 MB + ~150 MB) for zero benefit.
- **(b) invert the entry point** so the WinForms project hosts the console CLI — strictly more
  work than (d) for the same end state (one exe), plus it moves the ASP.NET minimal-API host
  into a `WinExe` and rewrites Phase 8's artifact identity. Rejected as a bigger change with
  no advantage.
- **(c) a shared "core" class library** — would solve a reference-direction problem that
  decision 1 makes non-existent, while still shipping two exes. Adds a project, fixes nothing.

## Root folders (`folder.json`)

Per the request: v1 config is a simple JSON array of absolute folder paths:
```json
["C:/projects"]
```
The **server** (not the UI) loads it at startup and exposes it read-only via a new route,
`GET /roots`, returning the paths verbatim (paths only, per the request — "show only the root
folder path").

**Location (decision 0, resolved).** Probe in this order and use the first file that exists
*and* parses as a JSON array of strings:
1. `%USERPROFILE%\.claude\glaude-folders.json` — the durable home, colocated with the
   `glaude-state.json` that `Cli/FileBackedStatusLineChainStore.DefaultPath()` already writes
   to `%USERPROFILE%\.claude\` **[VERIFIED-DISK]**.
2. `<directory of the running executable>\folder.json`.
3. `<current working directory>\folder.json` — this is what makes the request's
   `C:\projects\Glaude\folder.json` work during dev, since that is where `dotnet run`/the
   repo-root shell sits.

Missing/malformed/empty at every candidate → **empty array**, never a crash (same tolerant
philosophy as every other Glaude config path). A root path that does not exist on disk is
still listed (the UI shows it with zero sessions) rather than silently dropped — otherwise a
typo in the config looks identical to "no sessions yet". Paths are normalised for comparison
(`Path.GetFullPath`, trailing separator trimmed, `OrdinalIgnoreCase`) but rendered verbatim.

## Sessions per root folder (disk enumeration)

This is the part with no existing equivalent in the codebase — Phase 3d's `SessionState` only
knows about sessions this *specific running server instance* has received at least one event
for since it started. Listing "existing Claude sessions" under a folder needs to enumerate
**all sessions ever recorded on disk** for that folder, live or not.

### Claude Code's on-disk layout — **[VERIFIED-DISK]**, checked live on this machine

`%USERPROFILE%\.claude\projects\<slug>\<session_id>.jsonl` — one file per session. Live
check: exactly three project directories exist, `C--projects`, `C--projects-markdogwn-editor`,
`C--projects-swgen2`, and `C--projects` holds 33 `.jsonl` files plus per-session subdirectories
(`<session_id>\subagents\agent-<agent_id>.jsonl`, per `project.md` Source 4).

The `<slug>` is *derived from* the session's starting `cwd`. The previously stated rule — "`:`
and `\` both replaced by `-`" — is confirmed only for those two characters, and only because
every path on this machine happens to contain nothing else interesting: `C:\projects` →
`C--projects`, `C:\projects\markdogwn-editor` → `C--projects-markdogwn-editor`,
`C:\projects\swgen2` → `C--projects-swgen2` (each verified by reading the `cwd` field inside
the directory's own transcripts). **What happens to `.`, ` `, `_`, and non-ASCII characters is
[ASSUMED], not verified** — Claude Code's slugifier is widely observed to replace more than
just `:`/`\`. A root such as `C:\my.projects` could therefore encode to `C--my-projects`
rather than `C--my.projects`, and a forward-encoding matcher would silently show **zero**
sessions for it. Resolving this would need **[EXPERIMENT]**: start a session in a folder whose
name contains a dot, a space and an underscore, and read back the created directory name.

Because that experiment is not needed for correctness under the design below, it is *not* a
gate — but the design must not depend on the encoding rule being right.

**Known collision, inherent to Claude Code's encoding, not to Glaude:** since both `\` and a
literal `-` collapse to `-`, `C:\projects\foo` and `C:\projects-foo` encode to the *identical*
slug `C--projects-foo`. A slug-decoding or slug-prefix-matching design cannot tell them apart.

### Root attribution (decision 3, resolved) — match on `cwd`, not on the slug

**The slug is a hint; the transcript's own `cwd` field is the authority.**

1. Enumerate `%USERPROFILE%\.claude\projects\*` directories.
2. Optional cheap pre-filter: encode each configured root with the (unverified) rule and skip
   directories that are neither an exact match nor start with `<encoded-root>-`. This is a
   *performance* filter only. With three directories on this machine it can be skipped
   entirely; it must never be the sole basis for inclusion or exclusion.
3. For each `<session_id>.jsonl`, **head-read** the file (see below) and take the first
   `cwd` string found. That is the session's real starting directory
   (`"cwd":"C:\\projects"`, `"cwd":"C:\\projects\\markdogwn-editor"`,
   `"cwd":"C:\\projects\\swgen2"` — **[VERIFIED-DISK]**, one per directory).
4. A session belongs to a configured root iff its `cwd`, normalised, equals the root or is a
   path-segment-wise descendant of it (compare on separator boundaries, so `C:\projects` does
   **not** capture `C:\projects-foo`).
5. If several configured roots match (legitimately nested roots, e.g. both `C:\projects` and
   `C:\projects\swgen2` configured), attribute the session to the **longest** matching root.
   Deterministic, and strictly better than "whichever prefix matched first".
6. A session whose `cwd` cannot be read (truncated file, no entry carrying `cwd`) is attributed
   to **no** root and rendered in an explicit `(unattributed)` group rather than dropped —
   same "never silently lose a record" rule `SessionState.MarkAgentEnded`/`ReconcileLiveAgents`
   already follow.

Net effect: the slug collision **stops being a display bug**. `C:\projects-foo` sessions have
`cwd` = `C:\projects-foo`, which is not a segment-wise descendant of `C:\projects`, so they are
correctly excluded. The collision remains true of the *directory name* and is worth one line of
documentation, but it no longer changes what the UI shows. This also makes the unverified
encoding rule non-load-bearing.

### Head-read: a new reader, not `TranscriptReader`

`Metrics/TranscriptReader.cs` reads **only the last 64 KB** of a transcript
(`TailBytes = 64 * 1024`, seek to `length - TailBytes`) **[VERIFIED-DISK]**. It therefore
cannot supply either the starting `cwd` or the first user message. A **new** bounded
head-reader is required — same defensive contract as `TranscriptReader` (never throws;
`FileShare.ReadWrite` because Claude Code is appending; skip malformed/partial lines; return
null rather than propagate):

- Read the **first** ~64 KB, split on `\n`, discard a trailing partial line.
- First pass: first entry with a top-level `"cwd"` string → the session's root path.
  **Note:** the very first line is *not* usable — on this machine it is
  `{"type":"mode","mode":"normal","sessionId":"..."}` with no `cwd` **[VERIFIED-DISK]**. Scan
  forward; `cwd` appears on `user`/`assistant` entries.
- Second pass (same buffer, no extra I/O): first `"type":"user"` entry whose
  `message.content` is a plain string (or whose first content block is `{"type":"text"}`) →
  the display label, per decision 2.

### Session identifier and per-session fields

- **Identifier** = the `.jsonl` filename without extension (= `session_id`). Always available,
  always correct — no ambiguity, regardless of the slug-collision issue above.
- **Name (decision 2, resolved).** `session_name` exists **only** on the live `statusLine`
  payload **[VERIFIED-DISK]**: this session's own live payload carries
  `"session_name":"Build session monitoring application with model and effort tracking"`, and
  grepping all 33 transcripts under `C--projects` for `session_name`/`sessionName` yields
  **zero** matches. Nothing else on disk is usable either: the 39 `"title"` and 21 `"summary"`
  occurrences in those files belong to web-search tool results, not to the session
  **[VERIFIED-DISK]**. So:
  - **Live session** → the name Glaude captured from `statusLine`. This requires new work; see
    GAP A. It is *not* already captured.
  - **Historical session** → **derive a label from the first real user message** (head-read
    above), with these rules:
    - Truncate to 60 characters at a word boundary, collapse all whitespace to single spaces,
      strip control characters, render on one line.
    - **Skip wrapper entries** before picking one. The first `user` entry is frequently not a
      prompt at all — in this very session it is
      `<command-message>caveman</command-message><command-name>/caveman</command-name>…`
      **[VERIFIED-DISK]**. Skip entries whose text begins with `<command-message>`,
      `<command-name>`, `<local-command-`, `<system-reminder>`, or `[Request interrupted`, and
      take the next candidate. Give up after ~20 candidate entries.
    - Fallback chain: derived label → live `session_name` if present → first 12 chars of
      `session_id` (matching `SessionsView.IdTruncateLength = 12`).
  - **Why this is in-policy, not a new authorization.** Glaude already opens and parses whole
    transcript files, extracts assistant message metadata, and reads sibling `.meta.json`
    files; `project.md`'s posture throughout is *tolerant, read-only, in-memory, no
    persistence*. Reading one short prefix of one user message for a label is strictly less
    than what is already built. The line this design deliberately does **not** cross:
    the label is **display-only** — never written to disk, never logged, never sent anywhere
    (the server is loopback-only), never re-read for anything but the tree label, and length-
    capped so it cannot become a content dump. If a future reviewer wants even that removed,
    the fallback chain already degrades cleanly to the truncated `session_id`, so it is a
    one-line switch rather than a redesign.
- **Model / effort / context, for a NOT-currently-live session**: reuse the existing
  `TranscriptReader` (Phase 3b-ii) against the session's own main transcript file exactly as
  it's already used for subagent final records — last `"type":"assistant"` entry's
  `message.model` + top-level `effort.level` + `message.usage.*`. **Caveat inherited from the
  existing design, not new**: the transcript has token counts but **no context-window size**
  (statusLine-only), so a percentage requires `ModelWindowTable`'s prefix fallback rather than
  an observed number. Two further caveats the UI must respect, both **[VERIFIED-DISK]** in
  `Metrics/ModelWindowTable.cs`: its table is explicitly a *minimal placeholder* whose every
  entry is 200 000 except two `…-4-1m` rows, and `DefaultWindow` is a hard 200 000 with no
  "unknown" state. So every historical percentage is effectively "of an assumed 200 K window",
  and an extended-context id of the `claude-opus-5[1m]` shape matches nothing in the table and
  silently gets 200 000. **Render such percentages marked as assumed** (e.g. `31% (assumed
  200K)`), and prefer showing raw tokens next to it. Do not present them as observed.
- **Running vs not**: cross-reference the disk-enumerated `session_id` set against
  `SessionState.GetAllSessions()`. `status:"live"` in the server's current in-memory state
  (`StateQueryRoutes.ToDto`: `snapshot.Ended ? "ended" : "live"` — **[VERIFIED-DISK]**) means
  "currently running"; everything else found on disk is "not currently running". That includes
  sessions genuinely ended **and** sessions still open in another window that predate this
  `glaude run` process and haven't sent a fresh statusLine tick — an unavoidable consequence of
  `SessionState` being in-memory-only, already a deliberate documented design choice; this UI
  must not try to paper over it, but the window should say so once (see "Rendering").

## Server endpoints

Keep the UI a "dumb" renderer (same principle as the `sessions` CLI verb): the disk scan and
the merge with live state live **server-side**, reusing `TranscriptReader`/`ModelWindowTable`
and the new head-reader, mapped from `Server/StateQueryRoutes.cs` alongside the existing
`/sessions`, `/sessions/{id}`, `/agents`, `/state` routes.

**Alternative considered and rejected:** have the WinForms UI scan the filesystem itself. It
would duplicate the readers and the attribution rule, and it would *still* have to call the
server for liveness (only `SessionState` knows that) — two sources of truth for one tree.

### `GET /roots`

`200` + a JSON array of strings, verbatim from the config: `["C:/projects"]`. Empty array when
no config file is found. Never 404, never 500.

### `GET /roots/tree`

`200` + the entire hierarchy in one document, one call per refresh tick. Field names are
`snake_case` to match the existing DTOs. Nullable everywhere the underlying data is optional,
so a missing value renders as "unknown" rather than failing to deserialize — exactly the
convention `GlaudeStateClient`'s header comment already states.

```json
{
  "roots": [
    {
      "path": "C:\\projects",
      "exists": true,
      "sessions": [
        {
          "session_id": "22b04584-99e9-4343-b36d-8937b69321da",
          "name": "Audit the UI design doc",
          "name_source": "first_message",
          "cwd": "C:\\projects",
          "project_dir": "C--projects",
          "is_live": true,
          "status": "live",
          "model_id": "claude-opus-5[1m]",
          "model_display_name": "Opus 5",
          "effort_level": "high",
          "context_window_size": 1000000,
          "context_window_size_assumed": false,
          "used_tokens": 148223,
          "used_percentage": 14.8,
          "source": "statusLine",
          "as_of": "2026-08-13T10:24:53.755Z",
          "last_activity_utc": "2026-08-13T10:24:53.755Z",
          "agents": [
            {
              "agent_id": "abc123",
              "name": "Audit project-ui.md",
              "agent_type": "general-purpose",
              "model_id": "claude-sonnet-5",
              "effort_level": "medium",
              "input_tokens": 41200,
              "output_tokens": 3100,
              "cache_creation_input_tokens": 0,
              "cache_read_input_tokens": 0,
              "context_window_size": 200000,
              "context_window_size_assumed": true,
              "used_percentage": 20.6,
              "status": "live",
              "source": "subagentStatusLine",
              "as_of": "2026-08-13T10:24:52.100Z"
            }
          ]
        }
      ]
    }
  ],
  "unattributed_sessions": [],
  "unattributed_agents": [],
  "generated_at_utc": "2026-08-13T10:24:54.000Z",
  "scan_ms": 41
}
```

Contract details a plan writer needs, so nothing is guessed:
- `name_source` ∈ `"status_line" | "first_message" | "session_id"` — lets the UI mute a
  fallback label visually and makes decision 2 auditable from the wire.
- `status` reuses the existing vocabulary exactly: sessions `"live" | "ended"`; agents
  `"live" | "ended" | "stale"` (`StateQueryRoutes.StatusToString`). `is_live` is a convenience
  duplicate of `status == "live"` so the renderer needs no string comparison.
- `agents` is present **only** for sessions with `is_live == true`, and contains only agents
  whose own `status == "live"` — the request asks for *running* sub-agents of *active*
  sessions. For a historical session the property is an empty array, never null.
- `context_window_size_assumed: true` means the value came from `ModelWindowTable`, not from an
  observed `context_window_size`/`contextWindowSize`. Drives the "(assumed 200K)" annotation.
- `used_percentage` for agents is computed server-side as
  `(input + cache_creation + cache_read) / context_window_size * 100` — per `project.md`,
  input-only, and **never** accumulated across transcript entries. `AgentDto` does not carry a
  percentage today, so this is the route's job, not the UI's.
- `unattributed_sessions` / `unattributed_agents` mirror the shapes above and exist for the
  step-6 case and for live agents with no resolvable parent. `SessionsView.Render` already
  needs an "Agents (no matching session)" bucket for exactly this reason
  **[VERIFIED-DISK]** — the UI must have one too.
- Sort order is defined by the route, not the renderer: roots in config order; sessions live
  first, then by `last_activity_utc` descending (transcript file mtime for historical ones);
  agents by `as_of` descending. A stable order is a correctness property here, because the tree
  is rebuilt from scratch every tick.
- Never 500. A scan failure on one directory yields fewer sessions plus a non-fatal note, not
  an error response — same rule as every other handler in `StateQueryRoutes`.

### Per-tick cost and caching (new — the original design would re-parse everything)

A naive implementation re-reads every transcript on every 2 s tick: on this machine 33 files ×
(64 KB head + 64 KB tail) ≈ 4 MB of I/O and ~66 JSON scans **per tick**, forever, for data that
almost never changes. Required rule:
- Cache per absolute file path, keyed on `(length, LastWriteTimeUtc)`. Re-read a transcript
  only when that key changes. Directory *listings* are cheap enough to re-enumerate per tick
  (new sessions must appear promptly).
- `cwd` and the derived name come from the **head** of the file and are immutable for a
  session's lifetime — cache them permanently on first successful read; never re-read them.
- The cache is in-memory, non-persistent, bounded by the number of session files, and empty on
  restart — consistent with `project.md`'s "no persistence" constraint.

## Rendering

`System.Windows.Forms.TreeView`, **exactly three levels**: root folder → session → sub-agent
(sub-agents only under live sessions). Plus, when non-empty, one sibling top-level
`(unattributed)` node holding the two unattributed collections. The original "three or four
levels" was unspecific; there is no fourth level (nested sub-agents, `spawnDepth` 2 per
`project.md`, are flattened under their session in v1 — `AgentRecord` carries no
`parentAgentId`, so a deeper tree has no data behind it).

Per-node label carries the requested fields on one line, since this is non-interactive (no
detail pane in v1):
- root: `C:\projects` — path only, per the request (optionally `(4 sessions, 1 running)`).
- session: `● name — 22b04584… — Opus 5 — effort=high — 14.8% of 1M`
- agent: `agent-type · name — claude-sonnet-5 — effort=medium — 20.6% (assumed 200K)`

Live nodes are visually distinct: bold font plus a `●` prefix and the default fore colour;
historical/ended nodes use a muted `ForeColor` (e.g. `SystemColors.GrayText`) and no prefix.
`stale` agents get the muted style with a `?` prefix. Colour alone is never the only signal
(the prefix + weight carry it too). Icons are a nice-to-have, not required.

Refresh: a `System.Windows.Forms.Timer` (its `Tick` already arrives on the UI thread — no
manual `Invoke` needed for the callback itself) every ~2 s, matching `SessionsCommand`'s
`WatchInterval = TimeSpan.FromSeconds(2)` **[VERIFIED-DISK]**. The HTTP fetch must not block
the UI thread: `async void`/`async Task` handler, `await` the fetch, touch `TreeView` only
after it returns; guard against overlapping ticks (skip a tick while one is in flight) with
`TreeView.BeginUpdate()`/`EndUpdate()` around the rebuild.

Full-tree rebuild each tick is acceptable (same reasoning as the CLI's `--watch`). Preserving
expand/collapse state across a rebuild is a UX nit, not a correctness requirement; if done, key
it on the stable ids (`path` / `session_id` / `agent_id`), never on node index.

Because `SessionState` is in-memory-only, the window must state the caveat once rather than
implying omniscience — a status strip line such as
`live state as of {as_of}; sessions started before this server began are shown as historical`.
This is `project-plan.md`'s "render as-of, never current" requirement applied to the UI.

Unreachable server: render a single "Glaude server not reachable on port {port} — is
`glaude run` running?" node (the exact string `FetchResult<T>.AsUnreachable` already produces
**[VERIFIED-DISK]**) instead of crashing or showing a native exception dialog, and keep
polling so it recovers on its own — same tolerant behaviour as `SessionsCommand`'s watch loop.

## Packaging (decision 4, resolved)

Mirror Phase 8: `-r win-x64 -c Release`, `PublishSingleFile=true`, `SelfContained=true`, **no
trimming** (`project.md` Phase 8 already warns trimming breaks `System.Text.Json` reflection),
**plus `IncludeNativeLibrariesForSelfExtract=true`**.

Measured on this machine **[VERIFIED-BUILD]**:

| Configuration | Result |
|---|---|
| Today's `Glaude.exe` (`net8.0`, Sdk.Web, self-contained single-file) | **91.6 MB**, one file |
| + `net8.0-windows` + `UseWindowsForms` | 171.1 MB exe **plus 5 loose native DLLs** beside it (`D3DCompiler_47_cor3.dll`, `PresentationNative_cor3.dll`, `wpfgfx_cor3.dll`, `PenImc_cor3.dll`, `vcruntime140_cor3.dll`) |
| + `IncludeNativeLibrariesForSelfExtract=true` | **179.3 MB, genuinely one file** |
| Rejected two-project option | 91.6 MB + ~150 MB, two files |

So `IncludeNativeLibrariesForSelfExtract` is not a nicety: without it the "single executable"
property that Phase 8 exists to deliver quietly stops holding the moment WinForms is added. The
~88 MB increase is the `Microsoft.WindowsDesktop.App` runtime pack (it includes WPF natives
even when only `UseWindowsForms` is set); that is the price of one-file distribution and is
accepted. A framework-dependent build is **rejected** for consistency with Phase 8 and because
it would add a .NET-8-Desktop-runtime install prerequisite the tool currently doesn't have.

`publish.ps1` needs the `net8.0` → `net8.0-windows` path fix and the extra `-p:` flag; its
existing "expected executable not found" guard then covers the whole change.

## Completeness gaps found by this audit

Traced the way `project-plan.md` traced its Phase 3d gap: every requested field to a concrete
source *and* a concrete thing that exposes it. Three requirements dead-end in shipped code.

| Requirement (from the request) | Data source | Exposed today? |
|---|---|---|
| Root folder list, path only | new `folder.json` + `GET /roots` | new, fine |
| Per root: **all** sessions, live + historical | disk enumeration + `cwd` attribution | new, fine |
| Session **identifier** | `.jsonl` filename | fine |
| Session **name** | `statusLine` `session_name` (live only); first user message (historical) | **NO — GAP A** |
| Session **model / effort / context** | live: `SessionState` via `/state`; historical: `TranscriptReader` | fine (percentage is assumed-window) |
| Running sessions visually distinct | `SessionState` `status` | fine |
| Per live session: **running sub-agents nested under it** | `SessionState` agents keyed by `ParentSessionId` | **NO — GAP B** |
| Sub-agent **name** | `subagentStatusLine` `tasks[].name` | **NO — GAP C** |
| Sub-agent model / effort / context | `AgentRecord` via `/agents` | fine (no percentage on the DTO — computed by the new route) |

**GAP A — `session_name` is received and thrown away.** The `statusLine` payload carries
`session_name` (**[VERIFIED-DISK]** on the live payload, and listed in `project.md` Source 2),
but `Metrics/MetricsPipeline.cs`'s `HandleStatusLine` never reads it, `SessionSnapshot` has no
name field, and neither does `StateQueryRoutes.SessionDto` **[VERIFIED-DISK]**. The original
text of this doc asserted "for a currently-live session, use the name Glaude already captured"
— that is **false**. Required work, and it is server-side, not UI-side:
`HandleStatusLine` extracts `session_name` → new `SessionSnapshot.SessionName` → new
`session_name` field on `SessionDto` (and on the tree DTO). Without it, *every* session in the
window falls back to a derived-or-truncated-id label, live ones included.

**GAP B — live sub-agents have no parent, so they cannot nest (this is the analogue of the
Phase 3d gap).** `ParentSessionId` is set in exactly one place, `HandleSubagentStop`
(`GetString(root, "session_id")`); there is **no `SubagentStart` handler at all**, and
`HandleSubagentStatusLine` — the only source of *live* agent records — sets
`ParentSessionId: existing?.ParentSessionId`, i.e. `null` for any agent Glaude has not already
seen stop **[VERIFIED-DISK]**. Consequence: a currently-running sub-agent has a null parent,
`GET /sessions/{id}` filters agents by `ParentSessionId` and returns none, and the requested
"per-active-session hierarchical sub-list of running sub-agents" renders **empty** — every live
agent lands in the unattributed bucket. (That `SessionsView.Render` already ships an
"Agents (no matching session)" branch is evidence this happens in practice.) Required work:
in `HandleSubagentStatusLine`, take the payload's top-level `session_id` — the
`subagentStatusLine` body carries the base hook fields **[DOC]**, `project.md` Source 3 — and
use it as `ParentSessionId` (falling back to `existing?.ParentSessionId`, never overwriting a
known value with null). Cheap, and it is the difference between the feature working and not.
Secondary fallback if that field turns out to be absent **[EXPERIMENT]** — the Phase 7
`--dump-raw` capture already recorded real `subagentStatusLine` payloads, so this is a
five-minute check, not a new capture: `tasks[].cwd` is documented, and the subagent transcript
lives under `…\projects\<slug>\<session_id>\subagents\`, so the parent id is recoverable from
the path if needed.

**GAP C — sub-agent `name` is received and thrown away.** `tasks[]` carries `id`, **`name`**,
`type`, `status`, `description`, … (`project.md` Source 3), but `HandleSubagentStatusLine`
reads only `type` (into `AgentType`) **[VERIFIED-DISK]**; `AgentRecord`/`AgentDto` have no name
field. The request asks for sub-agents with "the same fields" as sessions, which includes a
name. Required work: capture `tasks[].name` (and optionally `description`) → new
`AgentRecord.Name` → new `name` on `AgentDto`/the tree DTO. Same shape of fix as GAP A.

None of these three is a UI problem; all three are one-field-each additions to the shipped
metrics pipeline and DTOs, and they must be scheduled *before* the WinForms work, or the window
will render structurally correct but empty columns.

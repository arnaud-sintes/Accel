# Glaude — Claude Code Session Activity Monitor

## Purpose

Native Windows C# CLI tool that monitors Claude Code local session activity by:
- Running a local, non-HTTPS HTTP server (default port **40010**, overridable via CLI arg).
- Auto-installing itself into Claude Code hooks (`%USERPROFILE%\.claude\settings.json`) so
  Claude Code events get forwarded to Glaude via `curl` POST calls.
- Printing received events to the terminal (v1 scope — no persistence, no UI beyond stdout).

## Stack decisions

- Language/runtime: C#, .NET 8, ASP.NET Core **minimal API** (chosen over raw `HttpListener`
  for simpler JSON route + model binding; still trims to a small single-file publish).
- Distribution: single-file self-contained `glaude.exe` (Windows x64), no install requirement.
- Server: plain HTTP (no TLS) — local loopback only, not exposed externally.

## Hook invocation contract (verified)

Verified against Claude Code hooks documentation (`https://code.claude.com/docs/en/hooks.md`)
and against the real `%USERPROFILE%\.claude\settings.json` on this machine.

**Two distinct mechanisms — do not conflate them:**

1. **`hooks`** — top-level object, keyed by event name, each value an array of
   *matcher groups*: `{ "matcher": "...", "hooks": [ { "type": "command", ... } ] }`.
2. **`statusLine`** — a **separate top-level field**, *not* inside `hooks`, and *not* an
   array. It is a single object: `{ "type": "command", "command": "...", "padding": N,
   "refreshInterval": N }`. Only one status line can exist.

**Invocation contract, common to both:**
- The event payload JSON **is automatically piped to the process's stdin** by Claude Code.
  The hook command does *not* need a wrapper to obtain it — so `curl ... -d @-` is valid.
- Env vars available: `CLAUDE_PROJECT_DIR`, `CLAUDE_PLUGIN_ROOT`, `CLAUDE_PLUGIN_DATA`,
  `CLAUDE_CODE_REMOTE`, `CLAUDE_EFFORT`. The payload is *not* passed via env vars or argv.
- Exit codes: `0` = success (stdout may be parsed as a JSON control object; stderr → debug
  log). `2` = **blocking error** (stderr fed back to Claude; for `SubagentStop` it forces the
  subagent to continue). Anything else = non-blocking error, first stderr line surfaced.
  → **Glaude hooks must always exit 0 and must never emit stdout**, otherwise a stray
  `curl` response body could be mis-parsed as a hook control object. Use `curl -s -o NUL`.
- Optional per-hook fields: `timeout` (seconds), `async` (boolean — fire-and-forget, already
  used by the existing `Notification` toast hook on this machine), `statusMessage`.
- **`SessionEnd` hooks share a ~1.5 s budget** by default. A synchronous curl to a dead port
  would eat that budget and delay shutdown → `SessionEnd` must be `"async": true` with a
  short `timeout`.

**Command execution form.** A hook entry may be:
- *shell form*: `"command": "<full command line>"` — run through `sh -c` when Git Bash is
  present, otherwise PowerShell. This is fragile on Windows: the command line must be
  JSON-escaped **and** shell-escaped, quoting differs between `sh` and PowerShell, and
  Windows paths need `\\` in JSON.
- *exec form*: `"command": "<exe>"` + `"args": [ ... ]` — spawned directly, no shell.

→ **Decision: Glaude uses exec form for all `hooks` entries.** This removes shell-quoting
ambiguity, sh-vs-PowerShell divergence, and most JSON-injection risk. (The existing
`Notification` toast hook in this settings.json already uses exec form, confirming support.)

## Hooks installed (Claude Code `settings.json`)

Glaude registers itself without touching unrelated existing entries (the existing
`PreToolUse` / matcher `Bash` → `rtk hook claude` entry must survive untouched):

| Key | Location | Matcher | Notes |
|---|---|---|---|
| `SessionStart` | `hooks` | `*` (all sources: startup/resume/clear/compact/fork) | exec-form curl POST |
| `SessionEnd` | `hooks` | `*` | exec-form curl POST, `async: true`, `timeout: 2` (1.5 s shared budget) |
| `SubagentStart` | `hooks` | `*` | exec-form curl POST. **Version-gated** — confirm the running `claude --version` emits it; if absent, Glaude degrades to SubagentStop only rather than writing a dead entry |
| `SubagentStop` | `hooks` | `*` | exec-form curl POST |
| `statusLine` | **top-level**, not `hooks` | n/a | see below — must chain, not clobber; needs `refreshInterval` |
| `subagentStatusLine` | **top-level**, not `hooks` | n/a | live per-subagent model/effort/context. Version-gated (v2.1.205 / v2.1.214). Safe to print nothing (default rows kept). Chain/restore like `statusLine` |

Payload shapes (stdin JSON), for the v1 pretty-printer (verified against hooks docs 2026-08):
- `SessionStart`: `session_id`, `prompt_id` (absent until first user input), `transcript_path`,
  `cwd`, `permission_mode`, `hook_event_name`, `source`, `model` (**optional — not guaranteed
  present**; the only hook event documented to ever carry `model`), plus `agent_id`/`agent_type`
  when the session *is* a subagent (`--agent` or in-subagent SessionStart).
- `SessionEnd`: `session_id`, `prompt_id`, `transcript_path`, `cwd`, `permission_mode`,
  `hook_event_name`, `reason`, plus `agent_id`/`agent_type` when inside a subagent.
- `SubagentStop`: `session_id`, `prompt_id`, `transcript_path`, `cwd`, `permission_mode`,
  `hook_event_name`, `agent_id`, `agent_type`, `last_assistant_message`. **No `model` field.**
  An `effort` field is **not documented** for this event (the docs publish no explicit
  `SubagentStop` schema) — treat it as *possibly present*, never required. See
  "Model/Effort/Context metrics sourcing".
- `SubagentStart`: same envelope minus `last_assistant_message` and `effort`. **No `model`.**
- Subagent events are *assumed* to carry the **subagent's own `transcript_path`** (separate
  file from the parent's). This is **not documented and not captured live** — see the
  path-derivation caveat in "Model/Effort/Context metrics sourcing".
- No hook payload carries token usage, cost, or context-window fields.
- Parsing must be tolerant: treat every field as optional and log the raw body on mismatch,
  since these schemas evolve between Claude Code releases.

`PreToolUse` intentionally **out of scope for v1** (deferred; must be appended as an
*additional matcher group* under the existing `PreToolUse` array, never replacing the
`rtk hook claude` group).

Hook entry shape (e.g. `SessionStart`):
```json
{
  "hooks": {
    "SessionStart": [
      {
        "matcher": "*",
        "hooks": [
          {
            "type": "command",
            "command": "curl.exe",
            "args": [
              "-s", "-o", "NUL", "--max-time", "2",
              "-X", "POST",
              "http://127.0.0.1:40010/events/session-start",
              "-H", "Content-Type: application/json",
              "-H", "X-Glaude-Hook: SessionStart",
              "-d", "@-"
            ],
            "timeout": 5,
            "statusMessage": "glaude"
          }
        ]
      }
    ]
  }
}
```

### `statusLine` — must chain, not clobber

`statusLine` stdout **is literally rendered as the status bar text**. A hook that POSTs and
prints nothing therefore **blanks the user's status bar** — a real, visible regression, and
it also destroys any pre-existing custom status line. "Returns empty/static text" is not
acceptable.

Also note `statusLine` has **no `args`/exec form** — the command is a shell string — so the
exec-form escape hatch is unavailable here. Rather than hand-building an escaped shell
one-liner (`sh -c` vs PowerShell divergence + Windows `\\` escaping), Glaude installs
**itself** as the status line:

```json
"statusLine": {
  "type": "command",
  "command": "\"C:\\path\\to\\glaude.exe\" statusline --port 40010"
}
```

`glaude statusline` reads the payload from stdin, POSTs it to `/events/status-line`
(fire-and-forget, short timeout), then reproduces the previous status line:
- If a pre-existing `statusLine` was found at install time, Glaude **saves its full original
  object** into its own state (see tagging below) and, when invoked, re-runs that original
  command with the same stdin payload and relays its stdout verbatim.
- If there was none, it emits Claude Code's rough default equivalent (model display name +
  current dir) rather than an empty string.
- Any failure (server down, chained command errors) must still print *something* and exit 0.
- The POST must be non-blocking and must never delay stdout. **Claude Code cancels an
  in-flight statusline script when a new update triggers** (updates are debounced 300 ms), so
  a slow Glaude invocation is killed, dropping both the event and the chained output. Spawn
  the POST detached and print as fast as possible.
- `statusLine` is **not** driven by a timer by default: it runs on session start/resume, new
  assistant message, `/compact`, permission-mode change, vim-mode toggle. Glaude must set
  `refreshInterval` (min `1`) — without it, no status-line event arrives while the main
  session waits on subagents, so main-session metrics go stale exactly when a subagent is
  running. Server-side de-duplication/throttling is still required.
- The statusLine stdin JSON is much richer than the hook payloads — it is the **primary source
  of main-session model/effort/context metrics** (see "Model/Effort/Context metrics sourcing"):
  `model.id`, `model.display_name`, `effort.level`, `cost.total_cost_usd`,
  `context_window.{total_input_tokens,total_output_tokens,context_window_size,used_percentage,
  remaining_percentage,current_usage{...}}`, `exceeds_200k_tokens`, `transcript_path`,
  `session_id`, `version`. Nullability/version gating applies — see that section.
- **These metrics come from the payload Glaude reads on stdin, never from the chained
  original command's stdout.** The chained stdout is opaque display text to be relayed
  verbatim; parsing it for metrics would be wrong (it may be ANSI-coloured, truncated, or
  produced by an unrelated third-party script).

`uninstall` restores the saved original `statusLine` object, or removes the field entirely
if there was none.

## Model/Effort/Context metrics sourcing

Investigated 2026-08 against the hooks + statusline docs and real transcript files on this
machine (Claude Code **2.1.224**, verified via `claude --version`). Hook payloads alone are
**insufficient** for per-session / per-subagent model, effort, and context consumption;
Glaude must combine several sources.

**Evidence tiers used below** — every claim in this section is tagged:
- **[VERIFIED-DISK]** — inspected in real files under `~/.claude/projects` on this machine.
- **[DOC]** — stated in the official hooks/statusline docs, not independently reproduced.
- **[ASSUMED]** — plausible inference, no evidence; must not be built on without a check.
- **[EXPERIMENT]** — needs a live capture (real session + subagent, raw payload dump).

### Source 1 — hook payloads (weakest)

- `model`: only on `SessionStart`, and optional **[DOC]** ("Only `SessionStart` hooks can
  receive a `model` field, and it is not guaranteed to be present"). Main session's model at
  start; can change mid-session (`/model`) with **no hook fired** — do not treat as
  authoritative after start.
- `effort` on `SubagentStop` **[DOC, weakly]**: the docs do *not* publish an explicit
  `SubagentStop` payload schema. They state only that effort is exposed for events firing in
  a tool-use context (`PreToolUse`, `PostToolUse`, `Stop`, `SubagentStop`). Whether that
  surfaces as a payload **field** named `effort` or only as the `CLAUDE_EFFORT` env var is
  **[EXPERIMENT]** — the previous revision of this doc asserted a payload field as fact; it
  is not documented as one.
- `CLAUDE_EFFORT` is set for `PreToolUse`/`PostToolUse`/`Stop`/`SubagentStop` **[DOC]**, not
  for `SessionStart`/`SubagentStart`/`SessionEnd`. **Moot for Glaude**: curl, not Glaude, is
  the hook process, so no hook currently sees any env var. Reading it would require switching
  to a `glaude hook <event>` exec form — a design change, not a free win.
- Token/context/cost: never present in any hook payload **[DOC]**.
- Attribution: `agent_id`/`agent_type` on subagent events **[DOC]**; that subagent events
  carry the **subagent's own** `transcript_path` rather than the parent's is **[ASSUMED]** —
  consistent with the on-disk layout below, but not stated in the docs and not captured live.

### Source 2 — statusLine stdin JSON (good for main session, *not* free)

Since Glaude installs itself as the status line, each invocation delivers, for the
**main session only** **[DOC]**: `model.id` + `model.display_name` (live, tracks `/model`),
`effort.level` (live, tracks `/effort`; absent if the model does not support effort),
`context_window` (`total_input_tokens`, `total_output_tokens`, `context_window_size` —
200000 default / 1000000 extended — `used_percentage`, `remaining_percentage`,
`current_usage`), `cost.*`, `exceeds_200k_tokens`, `fast_mode`, `thinking.enabled`,
`rate_limits.*`, `session_id`, `session_name`, `prompt_id`, `transcript_path`, `version`.

**Caveats the earlier "for free" framing glossed over — all [DOC]:**
- **It is not a timer.** Default triggers are: session start/resume, a new assistant message,
  `/compact` finishing, permission-mode change, vim-mode toggle. Docs explicitly warn the
  triggers "can go quiet when the main session is idle, for example while a coordinator waits
  on background subagents" → **main-session readings are stale for the entire duration of a
  subagent run** unless `refreshInterval` is set. Glaude must set `refreshInterval` (min 1 s)
  and treat every snapshot as *timestamped and possibly stale*, not "current".
- **Updates are debounced 300 ms, and an in-flight script is cancelled if a new update
  triggers.** Glaude's statusline process does two extra things (POST + re-invoke the chained
  original), so it is *more* likely than a plain script to be killed mid-flight. Consequences:
  status-line events may be silently dropped (never assume a complete series), and the chained
  original's output may be truncated. Ordering must be: read stdin fully → **spawn the POST
  fire-and-forget, do not await it** → run the chained command → print. Never POST-then-print.
- **`context_window` is version-gated and nullable.** Before v2.1.132 `total_*_tokens` were
  cumulative session totals, not current context; `current_usage` is `null` before the first
  API call and after `/compact`; `used_percentage`/`remaining_percentage` "may be `null` early
  in the session". `prompt_id` requires v2.1.196. Glaude must treat the whole object as
  optional/nullable and record the payload's `version` field alongside each snapshot.
- `used_percentage` is input-only (`input + cache_creation + cache_read`, excludes output).
- **Two different streams, do not conflate.** Metrics come from the JSON Glaude reads on
  **stdin**. The chained original status line's **stdout** is opaque display text that Glaude
  must relay byte-for-byte and must never parse for metrics. The current design is correct on
  this point; keep the distinction explicit in the implementation (separate buffers, no
  interleaving of Glaude's own diagnostics into stdout).

### Source 3 — `subagentStatusLine` (documented, first-class subagent metrics)

**[DOC]** A separate top-level setting `subagentStatusLine` (`{"type":"command","command":...}`)
runs once per refresh tick and receives **all visible subagent rows** as one JSON object:
base hook fields, `columns`, and a `tasks` array whose entries carry `id`, `name`, `type`,
`status`, `description`, `label`, `startTime`, **`model`** (resolved model id),
**`effort`** (level string *or* numeric token budget), **`contextWindowSize`**, **`tokenCount`**,
`tokenSamples`, `cwd`.

This is a **better source than transcript tailing** for live per-subagent model / effort /
context: it needs no hardcoded context-window table and no JSONL parsing. Gating and gaps:
- `model` + `contextWindowSize` require **v2.1.205+**; `effort` requires **v2.1.214+**
  (this machine is 2.1.224, so both are available — but Glaude must version-gate).
- `model`/`contextWindowSize` are omitted while a task's model is unresolved; `effort` is
  absent when the subagent **inherits** the session effort.
- It only shows **currently visible** rows — finished subagents disappear, so it cannot
  produce a final per-subagent record. `SubagentStop` + transcript remains needed for that.
- Its stdout protocol is different: one JSON line per row, `{"id": ..., "content": ...}`;
  **omitting a row keeps the default rendering**, so a pure-observer Glaude can print
  *nothing* here safely — unlike `statusLine`, this one has no blank-the-bar hazard.
- Chaining a pre-existing `subagentStatusLine` follows the same capture/restore rules as
  `statusLine`.

### Source 4 — transcript JSONL (authoritative final per-subagent record)

**[VERIFIED-DISK]** on this machine (43 subagent file pairs across several sessions):
- Main transcript: `~/.claude/projects/<proj-slug>/<session_id>.jsonl`.
- Subagents: `~/.claude/projects/<proj-slug>/<session_id>/subagents/agent-<agent_id>.jsonl`
  plus a sibling `agent-<agent_id>.meta.json`. **Both file kinds confirmed to exist.**
- `isSidechain: true` **confirmed present on entries in all 43** subagent files, and the main
  transcript inspected contained **zero** `isSidechain:true` entries → **per-file attribution
  is correct**; do not filter per-entry.
- Subagent entries also carry `agentId`, `parentUuid`, `attributionAgent`, `sessionId`,
  `version`, `gitBranch`, `cwd` — useful for correlation.

`.meta.json` — **correction to the previous revision.** Observed key tally over 43 files:
`agentType` 43, `spawnDepth` 43, `toolUseId` 32, `description` 32, `model` **29**,
`parentAgentId` 1. So:
- `model` is **optional (29/43)**, not guaranteed — it appears to be written only when the
  subagent's model is explicitly set, and is absent when the model is inherited. The previous
  text implied it is always there.
- Observed `model` values are aliases: `opus` (15), `sonnet` (8), `fable` (6). Alias-only is
  confirmed; the alias set is **[ASSUMED]** non-exhaustive.
- `toolUseId`/`description` are also optional; `parentAgentId` appears for nested spawns
  (`spawnDepth` 2).

`"type":"assistant"` entries, in both main and subagent JSONL:
- top-level **`effort`** — **[VERIFIED-DISK]**, present in 40/43 subagent files and in the main
  transcript (observed value `"low"`). Absent in the 3 files whose model does not support
  effort (haiku). This is a usable per-subagent effort source.
- **Downgraded to [ASSUMED]:** that this field reflects *per-call `opts.effort` overrides from
  the Agent tool*. Nothing on disk distinguishes an override from an inherited value; the
  previous revision stated this as fact.
- **`message.model`** — **[VERIFIED-DISK]**. **Correction:** values are *not* uniformly short
  ids. Observed: `claude-opus-5`, `claude-sonnet-5`, `claude-opus-4-8`, `claude-fable-5`,
  **and dated ids such as `claude-haiku-4-5-20251001`**. Any model→window lookup must handle
  both bare and date-suffixed forms (prefix match, longest-wins), not exact match.
- **`message.usage`** — **[VERIFIED-DISK]**: `input_tokens`, `output_tokens`,
  `cache_creation_input_tokens`, `cache_read_input_tokens`,
  `cache_creation.ephemeral_{1h,5m}_input_tokens`, `server_tool_use`, `service_tier`,
  `iterations[]`, plus (undocumented, present here) `inference_geo` and `speed`.

Context consumption = **last** assistant entry's
`input_tokens + cache_creation_input_tokens + cache_read_input_tokens` **[ASSUMED]** — this
matches the documented `used_percentage` formula for the main status line, but that the same
holds for the last transcript entry has not been cross-checked against a live status-line
reading. Do **not** accumulate across entries for context %; accumulate only for throughput.

Context-window **size** is not in the transcript **[VERIFIED-DISK]** — absent from `usage`.
Sources, in order of preference: `subagentStatusLine`'s `contextWindowSize` (v2.1.205+), then
statusLine's `context_window_size` for the main session, then a small hardcoded prefix table
(default 200000; 1000000 for extended-context `[1m]` models) with a fallback of
"unknown size → report raw tokens, no percentage".

### Path-derivation caveat — RESOLVED 2026-08-13 (live capture, Phase 7)

Captured real `SubagentStart`/`SubagentStop`/`statusLine`/`subagentStatusLine` payloads via
`glaude run --dump-raw` during a live install against this machine's real settings.json
(Claude Code 2.1.229), using two real subagents (one haiku, one sonnet). All five
**[EXPERIMENT]** items from the old "Recommended design" step 5 are now resolved:

1. **`agent_id` matches the `agent-<id>.jsonl` filename** — **[VERIFIED-LIVE]** confirmed.
   `agent_id` on both `SubagentStart`/`SubagentStop` is exactly the `<agent_id>` used in the
   subagent's transcript filename.
2. **`transcript_path` is the PARENT's transcript, not the subagent's — the old [ASSUMED]
   claim was wrong.** **[VERIFIED-LIVE], correction.** On every subagent event captured,
   `transcript_path` was identical to the *main session's* transcript path. The subagent's own
   transcript file is given by a **separate, previously undocumented field**:
   `agent_transcript_path`, present **only on `SubagentStop`** (absent on `SubagentStart`),
   e.g. `"...\\<session_id>\\subagents\\agent-<agent_id>.jsonl"`. This means: **no path
   derivation is needed at all** — `SubagentStop`'s payload hands the subagent transcript path
   directly. The old "Design rule" below (deriving `.meta.json` as a sibling of
   `transcript_path`) is superseded — derive `.meta.json` as the sibling of
   **`agent_transcript_path`**, not `transcript_path`.
3. **`SubagentStop` carries a top-level `effort` field**, object-shaped like statusLine's
   (`{"level":"medium"}`) — **[VERIFIED-LIVE]** confirmed present for a sonnet subagent,
   confirmed absent for a haiku subagent (consistent with haiku not supporting effort, not a
   missing-field bug). Distinct from the per-transcript-entry `effort` (same value observed in
   both places in this capture, but semantics are not proven identical in general).
4. **In-subagent `SessionStart`/`SessionEnd` hooks do NOT fire** — **[VERIFIED-LIVE]**. Only
   the manually-POSTed smoke-test `SessionStart` appeared in the capture; two real subagent
   runs produced zero additional `SessionStart`/`SessionEnd` events. No dedupe logic needed.
5. **`SubagentStart` never carries transcript info at all** (no `transcript_path` pointing at
   the subagent, no `agent_transcript_path` — that field only appears on `SubagentStop`) —
   **[VERIFIED-LIVE]**. Confirms the doc's expectation: at `SubagentStart` time, model/effort
   are unavailable from hook payloads; only `.meta.json` (if written) or `subagentStatusLine`'s
   live feed can supply them.
6. **Cancelled in-flight statusline POST loss — still [EXPERIMENT], not exercised** by this
   capture (would require forcing Claude Code to cancel a slow `glaude statusline` invocation
   mid-flight). Low priority: Phase 5's fire-and-forget-with-grace-period design already
   tolerates either answer.

Other fields observed on live `SubagentStop` payloads, not previously documented in this file:
`permission_mode`, `stop_hook_active`, `background_tasks`, `session_crons` (all present,
`background_tasks`/`session_crons` empty arrays in this capture — treat as optional/unknown
shape until seen non-empty).

### Recommended design (updated 2026-08-13 per the resolved items above)

1. Keep curl exec-form hooks as-is. On `SubagentStop`, the **server** reads the tail of the
   payload's **`agent_transcript_path`** (last few KB, parse trailing lines) — NOT
   `transcript_path`, which is the parent's — extracts the newest assistant entry's
   `message.model`, top-level `effort`, `message.usage`, and reads the **sibling** `.meta.json`
   (sibling of `agent_transcript_path`) for `agentType`/alias-`model`/`spawnDepth` — all fields
   optional. `SubagentStart` cannot supply any of this (item 5 above) — do not attempt to tail
   a transcript at `SubagentStart` time.
2. Main-session model/effort/context/cost come from the `/events/status-line` payload; keep a
   per-`session_id` latest-snapshot map, each snapshot stamped with receipt time and the
   payload's `version`, and rendered as "as of T" rather than "current".
3. Install `subagentStatusLine` (version-gated, v2.1.214+ for full fields) as the **live**
   per-subagent model/effort/context feed; print nothing to its stdout so default rows are
   preserved. Fall back to transcript tailing where it is unavailable or the task has ended.
4. Subagent percentage: `contextWindowSize` if available, else hardcoded prefix table,
   else no percentage.
5. Remaining open item: cancelled-statusline POST loss (item 6 above) — no longer blocking,
   revisit only if a real dropped-metric report surfaces in use.

## Install / detection / uninstall

- On startup, Glaude reads `%USERPROFILE%\.claude\settings.json` and diffs actual hook
  entries against the **expected** Glaude-tagged entries (including the currently
  configured port baked into the curl command).
- If missing, or port mismatch (tool started with `--port` different from what's registered):
  update settings.json in place — add missing hooks, rewrite Glaude's own entries with the
  new port. Non-Glaude hooks/entries must be preserved as-is.
- Glaude entries are identifiable via a stable marker. Concrete scheme:
  - Every event hook carries the header arg pair `-H "X-Glaude-Hook: <EventName>"` in its
    `args`. Matching on that literal arg is collision-safe (no other tool will emit it) and
    survives port changes.
  - `statusLine` ownership is detected by the command string containing the token
    `glaude` **and** the substring `statusline --port`.
  - Do **not** invent custom keys inside hook objects (e.g. `"glaude": true`) — Claude Code
    may reject or strip unknown fields.
- Port drift is detected by parsing the URL out of the registered `args` and comparing its
  port to the active one; mismatch → rewrite Glaude's own entries only.
- **Settings.json rewriting rules (correctness-critical):**
  - Read/modify/write via `System.Text.Json.Nodes.JsonNode` (DOM), never via a typed POCO —
    a POCO round-trip would silently drop unknown top-level keys (`env`, `permissions`,
    `theme`, `effortLevel`, `preferredNotifChannel`, …) present in the real file.
  - Serialize with `WriteIndented = true` and a non-escaping encoder so existing non-ASCII
    content is not mangled; write atomically (temp file in the same dir + `File.Replace`)
    and take a `.glaude.bak` copy before the first write.
  - Never string-concatenate into JSON. Build `JsonNode` values and let the serializer
    escape — this removes the JSON-injection and Windows `\\` path-escaping hazards.
  - If the file is missing/empty/malformed: for `install`, refuse and report rather than
    overwrite; only create it if absent entirely.
  - Removing a hook must prune empty containers: drop the `{matcher, hooks}` group if its
    `hooks` array becomes empty, and drop the event key if its array becomes empty — but
    never drop the whole `hooks` object if other events remain.
- CLI supports an explicit **uninstall** command that removes every Glaude-tagged hook entry
  from settings.json and restores the saved original `statusLine` (leaving all other
  hooks/config untouched) — for clean removal.
- Concurrency: Claude Code reads settings.json at session start and watches it for changes;
  Glaude must expect its writes to be picked up live, and must tolerate the file changing
  under it (re-read immediately before write; do not cache across the modify).

## HTTP routes (v1)

One route per event type, matching the hook set above:
- `POST /events/status-line`
- `POST /events/subagent-start`
- `POST /events/subagent-stop`
- `POST /events/session-start`
- `POST /events/session-end`
- `POST /events/subagent-status-line` (the `subagentStatusLine` `tasks` array; version-gated)

v1 behavior: parse JSON body (best-effort; log raw body if schema unknown), print a
human-readable line to the terminal (timestamp, event type, key fields). No auth, no HTTPS,
no on-disk storage/history. An in-memory current-state map (per "Clarification" under "Out
of scope (v1)") is maintained alongside the printer and exposed read-only — see Phase 3d.

## CLI surface (draft)

- `glaude` (or `glaude run`) — start server on default/configured port, self-check/install hooks.
- `glaude --port <n>` — start on custom port.
- `glaude uninstall` — remove all Glaude hook entries from settings.json, stop.
- `glaude status` — report current install state (installed? which port? hooks present?).
- `glaude statusline --port <n>` — **internal**, invoked by Claude Code as the status line:
  reads stdin, POSTs it, then prints the chained/original status line text. Must exit 0 and
  always print something.
- `glaude install` / `glaude repair` — apply hook registration without starting the server.

Server binds `127.0.0.1` explicitly (never `0.0.0.0`). If the port is already in use, fail
with a clear message (another Glaude instance is likely running) rather than silently
rewriting settings.json to a different port.

## Resolved questions

1. **Invocation contract** — resolved: payload arrives on **stdin as JSON**, automatically
   piped; no env-var or argv payload. See "Hook invocation contract (verified)".
2. **`statusLine` empty stdout** — resolved: **not safe**. stdout is the status bar text;
   empty output blanks it. Design now chains the pre-existing status line. Also corrected:
   `statusLine` is a top-level field, not a `hooks` event.
3. **curl availability** — `curl.exe` ships in `System32` on Windows 10 1803+ and Windows 11.
   Stated as a hard dependency; `install` probes for it and aborts with a clear message if
   absent. Note: on PowerShell shell-form, `curl` aliases to `Invoke-WebRequest` — always
   spell it `curl.exe`, which the exec form enforces anyway.
4. **Concurrency** — read-immediately-before-write + atomic replace + backup; see rules above.
5. **Marker scheme** — resolved: `X-Glaude-Hook: <EventName>` header arg; see above.

## Remaining open questions

1. Confirm `SubagentStart` is emitted by the *installed* Claude Code version (it is a newer
   event than `SubagentStop`). Install must version-gate rather than assume.

   -> **FEEDBACK**: YES, emitted

2. Whether `async: true` suppresses the stdout-as-control-object parsing entirely (assumed
   yes; irrelevant if hooks stay silent, which they must).

   -> **FEEDBACK**: YES

3. `refreshInterval` value for `statusLine` — tune to avoid flooding `/events/status-line`.

   -> **FEEDBACK**: YES

4. Whether Glaude should install into user settings (`~/.claude/settings.json`) only, or also
   support project-scope `.claude/settings.json`. v1: user scope only.

   -> **FEEDBACK**: user settings only, not project-scope

## Out of scope (v1)

- HTTPS / auth / remote access.
- Event **history** persisted to disk, filtering, UI beyond stdout/CLI.
- `PreToolUse` hook wiring (deferred to later iteration).

**Clarification (not out of scope):** an in-memory **current-state map** — one row per live
session/subagent, overwritten on each new snapshot, evicted on `SessionEnd`/`SubagentStop`/
vanished `subagentStatusLine` row — is required to satisfy the monitoring goal and is
in-scope for v1. This is *dynamic state management*, not persistence: nothing is written to
disk, there is no history/log/audit trail, and the map is empty again on restart. "No event
persistence" above means no on-disk history — it does not mean no queryable current state.

## Execution Plan

Ordered by dependency. "Agent" = delegate to a sub-agent; "Main" = do inline in the main
thread (needs full context or user interaction).

### Phase 1 — Scaffold

`Glaude.sln`, `src/Glaude/Glaude.csproj` (net8.0, `win-x64`, `PublishSingleFile`,
`SelfContained`, `InvariantGlobalization`), `tests/Glaude.Tests/` (xUnit), `.gitignore`,
placeholder `Program.cs`.
- **Who:** Agent. Purely mechanical, no design decisions.
- **Effort:** low. **Model:** Haiku — template generation, zero correctness risk; failures
  are immediately visible at build time.

### Phase 2 — Settings model + merge/diff/tag engine (the hard part)

`src/Glaude/Settings/SettingsFile.cs` (JsonNode load/atomic-save/backup),
`HookEntry.cs`, `GlaudeHookSpec.cs` (expected entries for a given port + exe path),
`SettingsMerger.cs` (`Detect` → installed/absent/port-drift/partial; `Install`; `Uninstall`),
`StatusLineChain.cs` (capture + restore the original `statusLine` **and**
`subagentStatusLine` — two independent top-level fields, same rules).
Plus `tests/Glaude.Tests/SettingsMergerTests.cs` with fixtures: empty file, real-world file
(the `rtk hook claude` + toast `Notification` one), pre-existing third-party `statusLine`,
already-installed, port drift, half-installed, malformed JSON, idempotent double-install,
install→uninstall round-trip byte-comparison against the original.
- **Who:** Main thread (with an Agent for the test fixtures once the API is fixed).
- **Effort:** high. **Model:** **Opus** — this is the only component that can destroy user
  configuration. It requires reasoning about DOM preservation, container pruning, tagging
  collisions, and round-trip invariants; a plausible-looking wrong answer here is silent
  data loss, which is exactly where the strongest model pays for itself.

### Phase 3 — HTTP server + event routes

`src/Glaude/Server/EventServer.cs` (minimal API, `UseUrls("http://127.0.0.1:{port}")`),
five `POST /events/*` routes reading the raw body, `EventPrinter.cs` (timestamped,
per-event-type formatting, tolerant field extraction, status-line de-duplication/throttle).
All routes return `204` with an empty body. **Transport and printing only** — the metrics
plumbing that was folded in here has been split out into the new Phase 3b, because it is a
different kind of work (tolerant parsing of undocumented, version-gated, partly-unverified
file formats) with a different failure mode.
- **Who:** Agent.
- **Effort:** medium. **Model:** Sonnet — standard ASP.NET Core work with a clear spec; no
  novel correctness hazard. *Unchanged* now that the risky part has moved to Phase 3b.

### Phase 3b — Metrics: transcript tailer + JSONL usage parser + session state (NEW)

Split out of Phase 3. Implements "Model/Effort/Context metrics sourcing":
`SessionState.cs` (per-`session_id` / per-`agent_id` snapshot map, each snapshot stamped with
receipt time + payload `version`, rendered "as of T" not "current"), `TranscriptReader.cs`
(bounded tail-read of the payload's `transcript_path`, last-assistant-entry extraction of
`message.model` / top-level `effort` / `message.usage`, sibling `.meta.json` read),
`ModelWindowTable.cs` (longest-prefix match, must handle both `claude-opus-5` and dated
`claude-haiku-4-5-20251001` forms, unknown → no percentage).
Hard requirements: every field optional; a partially written JSONL line (the file is being
appended to live) must never throw; per-file attribution via `isSidechain`, not per-entry;
never accumulate usage across entries for context %.
Plus a **payload-capture mode** (`glaude run --dump-raw <dir>`) to resolve the [EXPERIMENT]
items — this must land before the parser is finalised, since the `agent_id`↔filename and
`transcript_path`↔subagent-file mappings are assumed, not verified.
- **Who:** Agent, gated on the Phase 7 capture for the unverified mappings.
- **Effort:** medium–high. **Model:** **Sonnet, escalating to Opus** if the live capture
  contradicts the assumed payload→file mapping. Justification for not making this Haiku/plain
  Sonnet-low: the input formats are undocumented and version-dependent, several documented
  fields are nullable or absent by design, and the plausible-but-wrong failure mode here is
  *silently reporting confident wrong numbers* rather than crashing. It is still below
  Phase 2's Opus tier because nothing here can destroy user data — all reads.

### Phase 3c — `subagentStatusLine` (NEW)

Register a `subagentStatusLine` command (version-gated: `model`/`contextWindowSize` need
v2.1.205, `effort` needs v2.1.214), POST the `tasks` array to a new
`POST /events/subagent-status-line`, and **print nothing** so Claude Code's default rows are
preserved. Capture/restore any pre-existing `subagentStatusLine` using the Phase-2 chaining
machinery. This is the cheapest, best-quality live per-subagent metrics source and removes
the need to guess context-window sizes for subagents.
- **Who:** Agent.
- **Effort:** low–medium. **Model:** Sonnet — small surface, and unlike `statusLine` there is
  no blank-the-bar hazard (omitting a row keeps the default), so the risk is low. Depends on
  Phase 2 (chaining/restore) and Phase 6 (version gate).

### Phase 4 — CLI surface

`src/Glaude/Cli/` — arg parsing (`System.CommandLine` or a hand-rolled switch to keep the
single-file small), verbs `run` (default), `install`, `uninstall`, `status`, `statusline`,
global `--port`. `status` renders the Phase-2 `Detect` result.
- **Who:** Agent.
- **Effort:** low–medium. **Model:** Sonnet — plumbing, but it wires the dangerous Phase-2
  verbs, so it should not be done by the weakest tier.

### Phase 5 — `statusline` passthrough command

`src/Glaude/Cli/StatusLineCommand.cs`: read stdin to end, **spawn the POST detached and do not
await it**, then re-invoke the saved original command (feeding it the same stdin buffer) and
relay its stdout **verbatim, byte-for-byte** (never parsed for metrics — that comes from the
stdin payload); fall back to a synthesized default line. Guaranteed exit 0, guaranteed
non-empty stdout, all exceptions swallowed, no Glaude diagnostics ever on stdout.
Additional constraints discovered in review: Claude Code **cancels the in-flight script** when
a new update arrives (300 ms debounce), so total latency is a correctness property, not a
nicety — budget the whole command well under a refresh tick, and accept that some events are
lost by design (the server must not assume a complete series). Also set `refreshInterval` at
install time, otherwise no status-line event fires while the session waits on subagents.
- **Who:** Main thread.
- **Effort:** medium–high (raised: the cancellation semantics add a latency budget and a
  detached-child requirement on top of the original scope).
- **Model:** **Opus** (no longer "or strong Sonnet"). Justification: user-visible regression
  risk (blanked status bar on *every* refresh) now compounded by process-lifetime subtleties —
  a detached child that must outlive a killed parent on Windows, and stdin buffer re-feeding
  to an arbitrary third-party command.

### Phase 6 — curl probe + version gating

Detect `System32\curl.exe`; run `claude --version` and gate on it. Feeds the Phase-2
expected-entry set. Scope grew from one gate to a small version matrix (all thresholds are
documented, so this is lookup, not judgement):
| Feature | Minimum version |
|---|---|
| `SubagentStart` event | confirmed emitted on 2.1.224 |
| `subagentStatusLine` `model` / `contextWindowSize` | 2.1.205 |
| `subagentStatusLine` `effort` | 2.1.214 |
| `context_window.total_*` = current (not cumulative) | 2.1.132 |
| statusLine `prompt_id` | 2.1.196 |
Parse `claude --version` output (`"2.1.224 (Claude Code)"`) into a comparable version; an
unparseable version must degrade to the most conservative feature set, not crash.
- **Who:** Agent.
- **Effort:** low. **Model:** Haiku — still just process invocation plus version comparison
  against a table given verbatim above.

### Phase 7 — End-to-end manual validation

Publish, run `glaude install` against a **copy** of the real settings.json first, diff it,
then apply for real; start a fresh Claude Code session, spawn a subagent, exit; confirm all
event types print, confirm the `rtk hook claude` PreToolUse entry and the toast
`Notification` hook still fire, confirm the status bar still renders. Then `glaude uninstall`
and diff settings.json against the pre-install backup.
**Now also the evidence-gathering run for Phase 3b** — with `--dump-raw`, capture real
`SubagentStart`/`SubagentStop`/`statusLine`/`subagentStatusLine` payloads and resolve every
[EXPERIMENT] item listed in "Model/Effort/Context metrics sourcing": does `agent_id` match the
`agent-<id>.jsonl` filename, does the subagent payload's `transcript_path` point at the
subagent file, does `SubagentStop` carry an `effort` field, do in-subagent `SessionStart`
hooks fire, and does a cancelled statusline invocation lose its POST. Fold the answers back
into this document before Phase 3b is finalised.
- **Who:** Main thread — requires driving a real interactive Claude Code session and user
  judgement on the diffs; not delegable.
- **Effort:** medium–high (raised: it is now a data-gathering phase that unblocks 3b, not just
  a smoke test). **Model:** Opus (interactive) — it must recognise when a captured payload
  contradicts a documented assumption.

### Phase 8 — Packaging

`dotnet publish -r win-x64 -c Release -p:PublishSingleFile=true -p:SelfContained=true`,
trimming evaluation (careful: trimming + `System.Text.Json` reflection — prefer source-gen or
disable trimming), README usage section, a `publish.ps1`.
- **Who:** Agent.
- **Effort:** low. **Model:** Haiku, escalating to Sonnet only if trimming breaks JSON.

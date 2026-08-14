# Glaude — Execution Plan

Source of truth for design/decisions: `project.md`. This file tracks **execution order,
dependencies, gating, and status** only — do not duplicate design rationale here, link back
to `project.md` sections instead.

Legend: **Who** = Agent (delegate) / Main (inline, needs full context or user interaction).
Status: `TODO` / `IN PROGRESS` / `BLOCKED` / `DONE`.

## Gaps found in audit

Audit of this file against `project.md` (2026-08-13). What was wrong and what changed:

1. **GAP (blocking the stated goal) — nothing exposes an aggregated live view.**
   `project.md` → "HTTP routes (v1)" defines **only** `POST /events/*` routes whose v1
   behaviour is "print a human-readable line to the terminal. No storage, no auth" — there is
   **no GET route and no query surface at all**. `project.md` → "CLI surface (draft)" defines
   `glaude status` as *"report current install state (installed? which port? hooks present?)"*
   — i.e. installer state, **not** session state. Phase 3b builds `SessionState.cs` (a
   per-`session_id` / per-`agent_id` snapshot map) but **no phase ever exposes it**: it is an
   in-process map inside the server, and the CLI is a *separate process*, so even a new CLI
   verb cannot read it without an HTTP route. Net effect: once all 8 phases are executed, the
   tool delivers a **scrolling event log**, not "monitor model/effort/context for all
   currently running sessions and sub-agents". The goal is **not met** by the plan as written.
   → Added **Phase 3d — Aggregated state query surface** (below) and made Phase 4 depend on
   it. Owner rationale: `SessionState.cs` (Phase 3b) is the right home for the state, but the
   *exposure* (GET routes + CLI verb + eviction on `SessionEnd`/`SubagentStop`) is separable
   work with different dependencies, so it is its own phase rather than a Phase 3b bullet.
   **Amended in `project.md`** (2026-08-13): the current-state map is **in-memory only, not
   persisted to disk** — a live `session_id`/`agent_id` → latest-snapshot dictionary, evicted
   on `SessionEnd`/`SubagentStop`/vanished `subagentStatusLine` row, empty again on restart.
   No history/log/audit trail. This is *dynamic state management*, not "event persistence" —
   `project.md` → "Out of scope (v1)" now carries an explicit clarification saying so, and the
   HTTP routes section notes the map is maintained in-process and exposed read-only (Phase 3d).
2. **Circular dependency between Phase 3b and Phase 7.** The table had 3b depend on 7 and 7
   depend on 1–6 (which includes 3b). Real ordering: `--dump-raw` capture mode is *part of*
   Phase 3b (`project.md` Phase 3b: "Plus a payload-capture mode (`glaude run --dump-raw
   <dir>`) … this must land before the parser is finalised"). → Split into **3b-i** (capture
   mode, ships before Phase 7) and **3b-ii** (parser + `SessionState` + model-window table,
   gated on Phase 7's answers). Cycle removed.
3. **One [EXPERIMENT] item was dropped.** `project.md` → "Recommended design" item 5 lists
   (a) *two* assumptions, plus (b)(c)(d)(e) = **six** questions. This file listed five and
   omitted (b): whether `SubagentStart` fires early enough for the subagent JSONL to exist /
   contain an assistant entry. Restored as item 5 below. No invented items found.
4. **Dependency fixes in the table** (re-derived from what each phase produces/consumes):
   - Phase 6 had `—`; it is code in the solution → depends on **1**. It also *feeds* Phase 2's
     expected-entry set (version-gated `SubagentStart` / `subagentStatusLine`), so 6 must land
     before Phase 2's `Install` is final — recorded as `1 (6 before Install is final)`.
   - Phase 4 had only `2`; the `run` verb starts the Phase-3 server and the `statusline` verb
     is Phase 5's command → depends on **2, 3, 5**, and now **3d** for the live-view verb.
   - Phase 5 had only `2`; it POSTs to Phase 3's `/events/status-line` → **2, 3**.
   - Phase 8 omitted **6** from its dependency list.
   - Phase 3b-ii additionally consumes Phase 6's version gate (`context_window` semantics
     changed at 2.1.132; `prompt_id` at 2.1.196) → **3, 6, 7**.
5. **Second-order gap: liveness.** "Currently running" requires eviction. `SessionEnd` /
   `SubagentStop` / subagents disappearing from `subagentStatusLine`'s `tasks` array
   (`project.md`: "It only shows currently visible rows — finished subagents disappear") must
   mark entries as ended rather than leaving them in the live view forever. Assigned to
   Phase 3d. Also note `project.md`'s statusLine "de-duplication/throttle" (Phase 3) must be
   **per-`session_id`** — a global throttle would starve concurrent sessions, which directly
   breaks "all currently running sessions".
6. **Model/effort tiers: no mismatches found.** All ten rows match `project.md`'s stated tier
   and justification (Phase 2 / 5 / 7 Opus for destructive + user-visible-regression +
   interactive-judgement risk; Phase 1 / 6 / 8 Haiku as mechanical; Phase 3 / 3c / 4 Sonnet;
   Phase 3b Sonnet-escalating-to-Opus). The new Phase 3d is scoped Sonnet by analogy with
   Phase 3 (read-only aggregation, no destructive surface). Phase 2's **Who** was already
   correctly recorded as Main + Agent for fixtures.
7. **Dependency graph block rewritten** — the previous ASCII block read as a flat "Phase 1 ->
   everything" list and implied `Phase 4 -> Phase 8` / `Phase 3 -> Phase 3b -> Phase 7` chains
   that do not match the table.

## Dependency graph

```
Phase 1 (Scaffold)
  |
  +-> Phase 6 (curl probe + version gating) ------+
  |                                               |
  +-> Phase 2 (Settings engine) <-----------------+   (6 feeds 2's expected-entry set)
  |     |
  |     +-> Phase 3c (subagentStatusLine)  [also needs 6]
  |     +-> Phase 5 (statusline passthrough)  [also needs 3]
  |
  +-> Phase 3 (HTTP server + event routes)
        |
        +-> Phase 3b-i (--dump-raw capture mode)
        |     |
        |     v
        +--> Phase 7 (E2E validation + raw-payload capture)  [needs 1,2,3,3b-i,3c,5,6]
        |     |
        |     v
        +-> Phase 3b-ii (transcript tailer + JSONL parser + SessionState)  [also needs 6]
              |
              v
            Phase 3d (GET state routes + eviction)  -> Phase 4 (CLI surface) [also needs 2,3,5]
                                                            |
                                                            v
                                                       Phase 8 (Packaging)
```

Phase 7 both *validates* the whole stack and *unblocks* Phase 3b-ii's [EXPERIMENT] items
(payload-to-file mapping). Phase 3b-i must therefore ship **before** Phase 7; only 3b-ii is
gated on it.

## Phases

| # | Phase | Who | Effort | Model | Depends on | Status |
|---|---|---|---|---|---|---|
| 1 | Scaffold (`Glaude.sln`, csproj, test project) | Agent | low | Haiku | — | TODO |
| 2 | Settings model + merge/diff/tag engine | Main (+Agent for fixtures) | high | Opus | 1 (6 before `Install` is final) | TODO |
| 3 | HTTP server + event routes (transport/printing only) | Agent | medium | Sonnet | 1 | TODO |
| 3b-i | Raw payload capture mode (`glaude run --dump-raw <dir>`) | Agent | low | Sonnet | 3 | TODO |
| 3b-ii | Metrics: transcript tailer + JSONL usage parser + `SessionState` | Agent | medium–high | Sonnet, escalate Opus if capture contradicts assumptions | 3, 6, 7 | TODO |
| 3c | `subagentStatusLine` registration + route | Agent | low–medium | Sonnet | 2, 6 | TODO |
| 3d | **NEW (audit)** Aggregated state query surface: `GET /sessions`, `GET /sessions/{id}`, `GET /agents`, `GET /state`; liveness/eviction on `SessionEnd`/`SubagentStop`/vanished `tasks` rows | Agent | medium | Sonnet | 3, 3b-ii, 3c | TODO |
| 4 | CLI surface (`run`/`install`/`uninstall`/`status`/`statusline`) + new live-view verb (`sessions`/`watch`) querying Phase 3d over HTTP | Agent | low–medium | Sonnet | 2, 3, 3d, 5 | TODO |
| 5 | `statusline` passthrough command | Main | medium–high | Opus | 2, 3 | TODO |
| 6 | curl probe + version gating matrix | Agent | low | Haiku | 1 | TODO |
| 7 | End-to-end manual validation + raw-payload capture | Main | medium–high | Opus (interactive) | 1, 2, 3, 3b-i, 3c, 5, 6 | TODO |
| 8 | Packaging (single-file publish, trimming, README) | Agent | low | Haiku, escalate Sonnet if trimming breaks JSON | 2, 3, 3b-ii, 3c, 3d, 4, 5, 6 | TODO |

## Sub-agent invocation hints

Ready-to-use `Agent`/`Workflow` call parameters per phase — `model` + reasoning `effort` (the
tool's `effort` param: low/medium/high/xhigh/max), plus dispatch notes. Rule of thumb applied:
**task-effort tier is not reasoning-effort tier** — a mechanical low-effort task on the
strongest model still gets low reasoning effort; a hard task on Sonnet still gets high effort.
Read task-Effort (table above) and reasoning-effort as independent axes.

**Environment note:** `C:\projects` is **not a git repository** (confirmed live — `isolation:
"worktree"` on the `Agent` tool fails here with "Cannot create agent worktree: not in a git
repository"). Do **not** pass `isolation: "worktree"` for any phase below until Glaude's own
repo is git-initialized; run agents against the working tree directly.

| # | model | effort | subagent_type | isolation | Dispatch note |
|---|---|---|---|---|---|
| 1 | haiku | low | general-purpose | none | Template/scaffold generation against a fixed shape; verify by build, not review. |
| 2 | opus | high | general-purpose | none | **Do not fully delegate.** Keep Main driving; use an Opus agent only for isolated sub-steps (e.g. one fixture at a time) reviewed by Main before merge — this is the only phase that can destroy `settings.json`. |
| 3 | sonnet | medium | general-purpose | none | Standard ASP.NET minimal-API work against a written spec; low ambiguity. |
| 3b-i | sonnet | medium | general-purpose | none | Small, mechanical addition to Phase 3's server; spec is explicit (`--dump-raw <dir>`). |
| 3b-ii | sonnet | high | general-purpose | none | Start Sonnet/high (undocumented, version-gated, partly-unverified formats — wrong-but-plausible output is the failure mode, so push effort up even though model stays Sonnet). **Re-dispatch to opus/high** if Phase 7's captured payloads contradict any of the six [EXPERIMENT] items. |
| 3c | sonnet | medium | general-purpose | none | Small surface, no blank-the-bar hazard (unlike Phase 5); version-gate table is given, not derived. |
| 3d | sonnet | medium | general-purpose | none | Read-only aggregation over an already-defined `SessionState` shape; no destructive surface. |
| 4 | sonnet | medium | general-purpose | none | CLI plumbing, but wires Phase 2's dangerous verbs (`install`/`uninstall`) — do not drop to low effort. |
| 5 | opus | high | general-purpose | none | User-visible regression risk on *every* refresh (blanked status bar) plus Windows detached-child/stdin-refeed subtleties; keep Main reviewing the diff even if delegated. |
| 6 | haiku | low | general-purpose | none | Pure lookup: parse `claude --version`, compare against the version-matrix table given verbatim in `project.md`. |
| 7 | — (Main, interactive) | n/a | n/a | n/a | **Not delegable** — requires driving a real Claude Code session, judging live captures against the six [EXPERIMENT] items, and deciding whether 3b-ii escalates to Opus. If any sub-step is delegated (e.g. diffing two `settings.json` copies), use opus/high for that sub-step only. |
| 8 | haiku | low | general-purpose | none | Escalate the *specific re-dispatch* to sonnet/medium only if trimming breaks `System.Text.Json` reflection — do not pre-emptively raise the whole phase. |

Sequencing for a sub-agent-driven execution run: dispatch strictly in an order consistent with
the "Dependency graph" above (a phase's dependencies must be `DONE` before it is dispatched);
phases with no dependency edge between them (e.g. 1 and 6 after 1 lands, or 3b-i and 3c once
their deps clear) may be dispatched concurrently.

### Phase 3d scope (added by audit; `project.md` amended 2026-08-13)

Required for the stated goal (see "Gaps found in audit" #1). `project.md` now documents this
under "Out of scope (v1) — Clarification" and in the "HTTP routes (v1)" section.
- The state map itself is **in-memory, non-persistent, dynamic**: overwritten per snapshot,
  never written to disk, gone on process restart. Phase 3d is the *read-only exposure* of
  that map, not the map itself (the map is `SessionState.cs`, Phase 3b-ii).
- Expose the Phase-3b `SessionState` map read-only over the existing loopback server:
  `GET /sessions` (all sessions: `session_id`, model id/display name, effort level, context
  used/limit/percentage, cumulative input/output/cache tokens, snapshot age + payload
  `version`, `live|ended`), `GET /sessions/{session_id}` (that session + its agents),
  `GET /agents` (all subagents across sessions: `agent_id`, `agent_type`, parent
  `session_id`, `model`, `effort`, `tokenCount`, `contextWindowSize`, `status`),
  `GET /state` (everything, one JSON document).
- Every value carries its **source** (`statusLine` / `subagentStatusLine` / `transcript`) and
  an `as_of` timestamp — per `project.md`, snapshots are "as of T", never "current".
- Eviction/liveness: `SessionEnd` → session `ended`; `SubagentStop` → agent `ended` with the
  transcript-derived final record; an agent that disappears from `subagentStatusLine`'s
  `tasks` array without a `SubagentStop` → `stale`, not silently deleted.
- Still loopback-only, no auth, no HTTPS, **no history** — current state only, so the "no
  event persistence" v1 constraint is preserved in spirit.

## Open items carried from `project.md` that gate sign-off — RESOLVED 2026-08-13 (Phase 7 ran)

Phase 7 executed live: installed against the real settings.json on this machine (Claude Code
2.1.229), captured raw payloads via `--dump-raw`, spawned two real subagents (haiku, sonnet),
verified all six event types + existing `rtk hook claude`/`Notification` hooks survived +
statusLine/subagentStatusLine populated correctly, then uninstalled and confirmed a
byte-identical restore. Full detail in `project.md` → "Path-derivation caveat — RESOLVED".

1. `agent_id` matches the `agent-<id>.jsonl` filename — **confirmed**.
2. **Correction, not confirmation**: the payload's `transcript_path` is the **parent's**
   transcript, not the subagent's, on both `SubagentStart` and `SubagentStop`. The subagent's
   own file is a newly-discovered field, **`agent_transcript_path`**, present only on
   `SubagentStop`. Phase 3b-ii must use `agent_transcript_path`, not `transcript_path`, and
   needs no filename-reconstruction logic at all — simpler than originally planned.
3. `SubagentStop` **does** carry a top-level `effort` field (`{"level": "..."}`), present when
   the model supports effort (confirmed present for sonnet, confirmed absent for haiku) —
   **confirmed**.
4. In-subagent `SessionStart`/`SessionEnd` hooks do **not** fire — **confirmed no**, no dedupe
   logic needed.
5. `SubagentStart` never carries any transcript path (parent or subagent) — **confirmed no**,
   matches the expected fallback of model-from-`.meta.json`-only at start time.
6. Cancelled in-flight statusline POST loss — **still open, not exercised** by this capture.
   Downgraded to non-blocking: Phase 5's fire-and-forget-with-grace-period design tolerates
   either answer already.

Newly observed, previously undocumented `SubagentStop` fields (folded into `project.md`):
`permission_mode`, `stop_hook_active`, `agent_transcript_path`, `background_tasks`,
`session_crons`.

**Impact on Phase 3b-ii:** simplified, not escalated. The live capture confirmed the design
rather than contradicting it (aside from the `transcript_path` correction, which removes work
rather than adding it) — no escalation to Opus triggered. Model/effort tier stays Sonnet/high
per the original plan.

Not gating (already answered in `project.md` → "Remaining open questions" with FEEDBACK):
`SubagentStart` is emitted; `async: true` suppresses stdout control-object parsing;
`refreshInterval` is to be tuned; install is **user scope only**, no project-scope settings.

## Acceptance criteria (from stated goal)

The shipped tool must, for **every currently running session and its sub-agents**, surface:

| Criterion | Source (`project.md`) | Produced by | Exposed by |
|---|---|---|---|
| Main-session model id + display name | statusLine stdin `model.id`/`model.display_name` | 5 (capture) → 3 (route) → 3b-ii (`SessionState`) | **3d** |
| Main-session effort level | statusLine stdin `effort.level` | 5 → 3 → 3b-ii | **3d** |
| Main-session context usage + window size + used/remaining % | statusLine stdin `context_window.*` (nullable, version-gated) | 5 → 3 → 3b-ii, gate from 6 | **3d** |
| Main-session token consumption (input/output/cache) + cost | statusLine stdin `context_window.total_*` / `current_usage`, `cost.*` | 5 → 3 → 3b-ii | **3d** |
| Live subagent model / effort / `contextWindowSize` / `tokenCount` | `subagentStatusLine` `tasks[]` (v2.1.205 / v2.1.214) | 3c → 3 → 3b-ii, gate from 6 | **3d** |
| Finished-subagent final model / effort / usage | transcript JSONL + sibling `.meta.json` | 3b-ii (tailer), triggered by `SubagentStop` (3) | **3d** |
| Subagent → parent session attribution | `agent_id`/`agent_type` on hook payloads; `isSidechain` per file | 3 + 3b-ii | **3d** |
| "All running sessions at a glance" (aggregate view) | **no source in `project.md`** | — | **3d + 4** (added by audit) |

Per `project.md`, main-session values come from `statusLine`; live subagent values from
`subagentStatusLine`; finished-subagent final values from transcript JSONL + `.meta.json`.
No hook payload alone satisfies this — the combination is required.

**Every row above dead-ends at Phase 3d.** Without Phase 3d (or an equivalent addition to
Phase 3b), Phases 1–8 as originally written produce all the *data* and none of the *view*:
the last mile is a `Console.WriteLine` per event, and `glaude status` reports installer state
only. That is the single gap this audit found that blocks the stated goal.

Two further caveats that survive Phase 3d and must be stated in any UI/CLI output:
- Main-session readings **go stale while a subagent runs** unless `refreshInterval` is set,
  and are dropped when an in-flight statusline script is cancelled — the view must render
  "as of T", never "current" (`project.md` → Source 2 caveats).
- Model is not authoritative from hooks after session start (`/model` fires no hook), so the
  live view must prefer the statusLine snapshot over `SessionStart.model`.

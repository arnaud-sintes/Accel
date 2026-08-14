# Glaude UI — Execution Plan

Source of truth for design/decisions: `project-ui.md` (audited 2026-08-13). This file tracks
**execution order, dependencies, gating, and status** only — do not duplicate design
rationale here, link back to `project-ui.md` sections instead. Mirrors the structure of the
original `project-plan.md`.

Legend: **Who** = Agent (delegate) / Main (inline, needs full context or user interaction).
Status: `TODO` / `IN PROGRESS` / `BLOCKED` / `DONE`.

## Why the order is what it is

`project-ui.md`'s own "Completeness gaps" section is explicit: GAP A/B/C (session name,
sub-agent parent linkage, sub-agent name) are **server-side metrics-pipeline bugs**, not UI
work, and "must be scheduled *before* the WinForms work, or the window will render
structurally correct but empty columns." The plan below schedules them first for exactly that
reason. The `net8.0-windows`/`UseWindowsForms` csproj migration (decision 1) is a build-config
change with no logical dependency on the metrics fixes, so it runs in parallel with them —
but nothing that touches `System.Windows.Forms` can compile until it lands.

## Dependency graph

```
Phase UI-A (metrics gap fixes: A/B/C)   Phase UI-B (head-reader)   Phase UI-C (roots config + GET /roots)
        |                                     |                              |
        +----------------+--------------------+------------------------------+
                          |
                          v
                 Phase UI-D (GET /roots/tree: attribution, merge, caching)
                          |
        Phase UI-E (csproj/TFM migration) ----+
                          |                   |
                          v                   v
                 Phase UI-F (ui verb + WinForms window + rendering)
                          |
                          v
                 Phase UI-G (packaging: publish.ps1, README)
                          |
                          v
                 Phase UI-H (E2E validation — Main, interactive)
```

Phase UI-A, UI-B, UI-C have no dependency on each other and should be dispatched together.
Phase UI-E has no dependency on UI-A/B/C/D and may be dispatched at the same time as them, but
must complete before UI-F (which is the first phase to actually reference
`System.Windows.Forms`).

## Phases

| # | Phase | Who | Effort | Model | Depends on | Status |
|---|---|---|---|---|---|---|
| UI-A | Metrics pipeline gap fixes (GAP A: `session_name`; GAP B: live sub-agent `ParentSessionId`; GAP C: sub-agent `name`) | Agent | medium | Sonnet | — | TODO |
| UI-B | Bounded head-reader (`cwd` + first-user-message label, per decision 2/3) | Agent | medium | Sonnet | — | TODO |
| UI-C | `folder.json` probe/load + `GET /roots` | Agent | low | Sonnet | — | TODO |
| UI-D | `GET /roots/tree`: disk enumeration, `cwd`-based attribution (longest-root-wins), merge with live `SessionState`, `ModelWindowTable` percentages, per-tick caching, unattributed buckets, sort order | Agent | high | Sonnet, escalate Opus if attribution/caching correctness issues surface in testing | UI-A, UI-B, UI-C | TODO |
| UI-E | `Glaude.csproj` → `net8.0-windows` + `UseWindowsForms` + `IncludeNativeLibrariesForSelfExtract`; migrate `Glaude.Tests.csproj` TFM; fix `publish.ps1` path | Agent | medium | Sonnet | — | TODO |
| UI-F | `ui` verb (`ArgParser`/`Program.cs`), `GlaudeStateClient` additions (`GetRootsAsync`/`GetRootsTreeAsync`), WinForms `Form` + `TreeView`, refresh timer, live/historical styling, unreachable-server handling | Agent | high | Sonnet | UI-D, UI-E, UI-C | TODO |
| UI-G | Packaging: republish with new csproj settings, verify single-file (~179 MB per audit measurement), update `publish.ps1`/README | Agent | low | Haiku | UI-F | TODO |
| UI-H | End-to-end validation: run `glaude ui` against the real live server, confirm live/historical/stale rendering, refresh, caching, unreachable-server fallback | Main | medium | Opus (interactive) | UI-G | TODO |

## Sub-agent invocation hints

Same convention as `project-plan.md`: `model` + reasoning `effort` (the `Agent`/`Workflow`
tool's `effort` param, independent from the task-effort tier in the table above), plus
dispatch notes. Environment note carried forward: `C:\projects` is **not a git repository** —
do not pass `isolation: "worktree"` for any phase below.

| # | model | effort | subagent_type | isolation | Dispatch note |
|---|---|---|---|---|---|
| UI-A | sonnet | medium | general-purpose | none | Three small, well-specified field-plumbing additions (`SessionSnapshot.SessionName`, `HandleSubagentStatusLine`'s `ParentSessionId` source, `AgentRecord.Name`) across `Metrics/MetricsPipeline.cs`, `Metrics/SessionState.cs`, `Server/StateQueryRoutes.cs`. Low ambiguity — `project-ui.md`'s GAP A/B/C sections give exact field names and exact current (wrong) behavior. Must not regress the 191 existing tests. |
| UI-B | sonnet | medium | general-purpose | none | New reader, same defensive contract as the already-shipped `TranscriptReader` (copy its never-throw/`FileShare.ReadWrite`/skip-partial-line conventions). The "skip wrapper entries" list is given verbatim in `project-ui.md` — no invention needed, just correct implementation and tests against real transcript shapes. |
| UI-C | sonnet | low | general-purpose | none | Small, mechanical: three-candidate-path probe + JSON-array parse + tolerant fallback to empty array. Same tolerant-config pattern already used for `glaude-state.json`. |
| UI-D | sonnet | high | general-purpose | none | The highest-risk *data-correctness* phase in this plan (not destructive, but a silently-wrong attribution or a caching bug is the "confident wrong answer" failure mode `project-plan.md` used to justify high effort for Phase 3b-ii). Push effort up even though the model stays Sonnet. **Re-dispatch at opus/high** only if testing surfaces a genuine attribution/caching correctness issue that resists a targeted fix. |
| UI-E | sonnet | medium | general-purpose | none | Build-config change touching the whole solution (`Glaude.csproj`, `Glaude.Tests.csproj`, `publish.ps1`). Not conceptually hard, but must run the full 191-test suite afterward and fix any TFM-migration fallout rather than assuming it's clean — `project-ui.md` already flags the two known one-line fixes (test csproj TFM, publish.ps1 path), verify nothing else broke. |
| UI-F | sonnet | high | general-purpose | none | Genuine complexity (UI-thread marshaling, async fetch inside a `Timer.Tick`, overlapping-tick guard, `TreeView` rebuild) but no destructive-data or corrupt-shared-state risk — worst case is a wrong/empty window, which is cheaply visible and fixable, unlike Phase 2/5's settings.json/status-bar blast radius in the original plan. That asymmetry is why this stays Sonnet rather than escalating to Opus by default. |
| UI-G | haiku | low | general-purpose | none | Mechanical republish + doc update; the target numbers (single-file, ~179 MB) are already measured in `project-ui.md`'s audit, this phase just confirms them again post-implementation. |
| UI-H | — (Main, interactive) | n/a | n/a | n/a | **Not delegable** — requires actually looking at a rendered window. No screenshot/GUI-inspection tool is confirmed available in this session; if one turns out to be reachable via `ToolSearch`, use it, otherwise drive the verb via terminal (`glaude ui &`, then poll `/roots/tree` directly to cross-check what *should* render) and ask the user to visually confirm the window itself looks right. |

## Acceptance criteria (from the original request)

| Requirement | Delivered by | Verified by |
|---|---|---|
| Root folder list, path only | UI-C (`GET /roots`) + UI-F (render) | UI-H |
| Per root: sub-list of **all** existing sessions (name, identifier, model, effort, context) | UI-B + UI-D (`GET /roots/tree`) + UI-F (render) | UI-H |
| Running sessions shown differently | UI-D (`status`/`is_live`) + UI-F (styling) | UI-H |
| Per active session: hierarchical sub-list of **running** sub-agents (name, identifier, model, effort, context) | UI-A (GAP B parent fix, GAP C name) + UI-D (nesting + percentage) + UI-F (render) | UI-H |

Every row depends on UI-A/UI-B/UI-D landing correctly — per `project-ui.md`'s own audit,
skipping them produces a window that opens and looks plausible while silently showing empty
names and zero nested sub-agents. UI-H is the only phase that closes the loop and must not be
skipped or treated as a formality.

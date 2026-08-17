# Agent Graph (Panel E) — Duration / Token Data-Model Audit

Scope: audit only. No code changed. Covers what Accel's existing pipeline (`Server` → `Metrics` →
`Cli.MonitorTreeBuilder` → `App.ViewModels.RootsPanelViewModel`) can and cannot already tell Panel E
about **duration** and **consumed tokens**, per session and per sub-agent, so the future Agent Graph
control has a real data contract to render instead of guessing. Model/Effort/Context are already
solved end-to-end and are not re-audited here beyond noting where they live.

Companion open question (from the original ask, answered here for the record, not re-derived):
Claude Code's hook surface today is only `SessionStart` / `SessionEnd` / `SubagentStart` /
`SubagentStop` / `statusLine` / `subagentStatusLine` (`Versioning/VersionGate.cs`'s `Feature` enum).
There is no hook event for the CLI's internal workflow-phase/step progression — it's rendered
client-side inside the `claude` process and never posted anywhere. Nothing to hook today; revisit if
Claude Code ever adds a phase/step hook.

## 1. What already exists, end to end

| Field | Session | Agent | Source |
|---|---|---|---|
| Model | `SessionSnapshot.ModelId`/`ModelDisplayName` | `AgentRecord.ModelId` | `statusLine` payload / transcript tail / `subagentStatusLine` task |
| Effort | `SessionSnapshot.EffortLevel` | `AgentRecord.EffortLevel` | same as above |
| Context window + used % | `ContextWindowSize`, `UsedTokens`, `UsedPercentage` | `ContextWindowSize`, tokens (below) | same as above, `ModelWindowTable` fallback |

All three already flow through `RootsTreeBuilder` → `SessionTreeDto`/`AgentTreeDto` →
`Cli.MonitorTreeBuilder.MonitorRowColumns` → `RootsPanelNodeViewModel` (`ModelBadge`,
`EffortBarLevel`, `Columns.Context`) and are already rendered in panel A. The Agent Graph can reuse
these classes/columns unchanged for Model/Effort/Context.

## 2. Duration — audit findings

**No start timestamp is captured anywhere in the live pipeline today.** Every timestamp Accel
currently stores is a *receipt* time, not a *start* time:

- `SessionSnapshot.ReceivedAtUtc` — stamped `DateTime.UtcNow` when a `statusLine` POST is *handled*
  (`MetricsPipeline.HandleStatusLine`), i.e. every status-line tick overwrites it. Not the session's
  start time.
- `AgentRecord.ReceivedAtUtc` — same pattern: stamped `DateTime.UtcNow` in `HandleSubagentStop`,
  `HandleSubagentStatusLine`, and `MarkAgentEnded`. Also a receipt/last-update time, not a start time.
- `RootsTreeBuilder`'s historical (non-live) path uses `FileInfo.LastWriteTimeUtc` (`asOf`,
  `LastActivityUtc`) — the transcript file's last-modified time, again an end/last-activity marker,
  not a start marker.
- `EventPrinter.Print` computes its own `DateTime.Now` purely for the console log line; it is never
  stored.

**`SessionStart` and `SubagentStart` hook events are received but completely unparsed.**
`Server/EventServer.cs`'s `HandleEventAsync` only special-cases `SubagentStop` (metrics) and
`SessionEnd` (mark-ended); `SessionStart`/`SubagentStart` fall through to `EventPrinter.PrintEvent`
only — no `SessionState` mutation, no stored timestamp, nothing. This is the single biggest gap: the
one moment Accel is told "this session/agent just started" is currently discarded after being
printed to the console.

**Real hook payloads carry no explicit timestamp field either**, per every fixture in the test suite
(`RawPayloadCaptureTests.cs`, `EventServerTests.cs`, `SettingsMergerTests.cs`) —
`{"session_id":"...","hook_event_name":"SessionStart"}` and
`{"session_id":"...","agent_id":"...","hook_event_name":"SubagentStop"}` are the full observed
shapes; none of Claude Code's hook bodies include a `timestamp`/`started_at` field. So even if
`SessionStart`/`SubagentStart` were wired up, the *only* timestamp available for "when did this
start" would be **Accel's own receipt time** (`DateTime.UtcNow` at the moment the POST lands), not a
value from the payload itself.

**The transcript JSONL itself does carry real per-entry timestamps** — confirmed against a live
transcript on this machine (every line, regardless of `type`, has a top-level
`"timestamp":"2026-08-17T10:04:26.964Z"` ISO-8601 field) — but nothing in `Metrics/` reads it today:
- `TranscriptHeadReader` extracts `cwd` and the first user message text from the head window, but
  never reads `timestamp` off the first line (which would be the session's true start time).
- `TranscriptReader.TryReadLastAssistantEntry`/`TryReadLastAiTitle` extract the tail window's last
  assistant entry / ai-title, but never read that entry's `timestamp` (which would be the session's
  most recent real activity time — a better "last activity" signal than file `LastWriteTimeUtc`,
  though in practice close to it).
- Sub-agent transcripts (`agent-<id>.jsonl`) are the same JSONL shape, so the same head/tail
  timestamp extraction would apply there too. `SubagentMetaInfo` (the sibling `.meta.json`) has no
  timestamp field at all (`AgentType`, `SpawnDepth`, `ToolUseId`, `Description`, `Model`,
  `ParentAgentId` only).

**Net conclusion — two independent, non-overlapping ways to get a start time exist, neither wired up:**
1. Wire `SessionStart`/`SubagentStart` handling in `EventServer`/`MetricsPipeline` to stamp
   `DateTime.UtcNow` into a new `StartedAtUtc` field the moment the hook fires. Cheapest, but only
   covers sessions/agents that start *while Accel is running* (a session started before Accel's
   process launched, or before the hook was installed, has no `SessionStart` to catch) — same
   "historical vs. live" split `MonitorNodeState` already models.
2. Read the transcript's own first-line `timestamp` (head window) as a durable, always-available
   start time, including for sessions Accel never saw start live. This is strictly more complete
   than (1) but costs a head-window read Accel isn't currently doing for this purpose (though it
   already opens the head window for `cwd`/first-message — extending that same read to also capture
   `timestamp` is incremental, not a new I/O path).

Recommendation for the eventual data model: prefer (2) as the source of truth for `StartedAtUtc`
(covers every session/agent, live or historical, exactly like `Cwd` does today), and treat (1) as
unnecessary once (2) exists — there is no case (1) covers that (2) doesn't.

"Duration" itself is then simply `(IsLive ? DateTime.UtcNow : LastActivityUtc) - StartedAtUtc`, once
`StartedAtUtc` exists — no new concept beyond that subtraction.

## 3. Tokens — audit findings

Raw token counters already exist and are richer than what's surfaced today, but there is **no single
"consumed tokens" total** anywhere — every layer stops at the raw input/output/cache breakdown:

- `AgentRecord`: `InputTokens`, `OutputTokens`, `CacheCreationInputTokens`, `CacheReadInputTokens` —
  four separate `int` fields, populated from `TranscriptAssistantEntry` (SubagentStop path) or from
  a `subagentStatusLine` task's `tokenCount` (folded entirely into `InputTokens`, with
  `OutputTokens`/cache fields left at whatever was already stored — see
  `MetricsPipeline.HandleSubagentStatusLine`, which never receives an output/cache breakdown from
  that payload shape at all).
- `SessionSnapshot`: only a single derived `UsedTokens` (`long?`), already summed as
  `input + cache_creation + cache_read` in `MetricsPipeline.HandleStatusLine` — **note this
  deliberately excludes `OutputTokens`**, because the `statusLine` payload's `context_window.
  current_usage` object has no output-token field (context window usage is an input-side concept -
  output tokens generated so far aren't "sitting in context" the same way). This is correct for
  *context-window percentage* but would be a silent undercount if reused naively as "total tokens
  consumed by this session."
- `AgentTreeDto`/`SessionTreeDto` (the wire DTOs): expose the same fields verbatim
  (`AgentTreeDto.InputTokens/OutputTokens/CacheCreationInputTokens/CacheReadInputTokens`,
  `SessionTreeDto.UsedTokens`), still no rollup.
- `Cli.MonitorTreeBuilder`/`MonitorRowColumns`: collapses all of the above down to a single
  `Context` string like `"12.3% of 1M (assumed)"` — the percentage only, raw counts and any token
  total are dropped entirely by the time a row reaches `RootsPanelViewModel`/panel A. **This is the
  actual proximate gap for the Agent Graph**: even though the raw numbers exist upstream, nothing
  between `RootsTreeBuilder` and the UI carries them forward today.

**Net conclusion:** no `MetricsPipeline`/`SessionState` change is strictly required to get a
"consumed tokens" figure — the raw counters are already there. What's missing is (a) a clear,
named "total consumed" definition (recommend `input + output + cache_creation + cache_read` for a
genuine consumption total, kept **distinct** from the existing `UsedTokens`/`UsedPercentage`
context-window figure, which must keep excluding output tokens to stay correct), and (b) plumbing
that total through `MonitorRowColumns`/`AgentTreeDto`/`SessionTreeDto` (or a new column) down to the
Agent Graph's node view models, since `MonitorRowColumns` today only carries the pre-formatted
percentage string.

One asymmetry to note for whoever builds the model: `SessionSnapshot` never gained the
input/output/cache-split fields `AgentRecord` has (`statusLine` payloads apparently don't provide an
output-token count at all, only `current_usage`'s input-side breakdown per
`MetricsPipeline.HandleStatusLine`) — so a session's "consumed tokens" total, unlike an agent's, can
only ever be the input+cache figure already in `UsedTokens`, not a true input+output total, unless a
future `statusLine` payload version adds an output-token field. This should be surfaced in the UI
(e.g. a tooltip caveat) rather than silently presented as equivalent to an agent's total.

## 4. Summary gap table

| Need | Exists today? | Where it would come from |
|---|---|---|
| Model / Effort / Context % | Yes, fully wired | `MonitorRowColumns` (already in panel A) |
| Session start time | No | New: transcript head-window `timestamp` (recommended) or `SessionStart` hook receipt time |
| Agent start time | No | New: sub-agent transcript head-window `timestamp` (recommended) or `SubagentStart` hook receipt time (currently unparsed) |
| Session/agent end or last-activity time | Partial (`LastActivityUtc`/`AsOf` = file mtime or receipt time) | Could be tightened to the transcript's own last-entry `timestamp` instead of file mtime, but close enough already |
| Duration | No (derived metric, blocked on start time) | `(now or last-activity) - StartedAtUtc` once start time exists — **Closed, see section 6/implementation**: `SessionTreeDto`/`AgentTreeDto.DurationMs`, computed in `RootsTreeBuilder` via the shared `ComputeDurationMs` helper. |
| Agent consumed tokens | Raw counters exist, no rollup surfaced past `RootsTreeBuilder` | Sum `AgentRecord`'s four counters; add to `MonitorRowColumns`/view model — **Closed, see section 6/implementation**: `AgentTreeDto.ConsumedTokens`, plumbed through `MonitorRowColumns.Tokens`/`MonitorAgentNode.ConsumedTokens`/`RootsPanelNodeViewModel.ConsumedTokens`. |
| Session consumed tokens | `UsedTokens` exists (input+cache only, no output) | Reuse `UsedTokens`, but label it accurately (context-window usage, not a true total) — **Closed, see section 6/implementation**: `SessionTreeDto.ConsumedTokens` (= `UsedTokens`) plus `ConsumedTokensIsContextOnly = true` so the UI can render the caveat instead of treating it as comparable to an agent total. |
| Orchestration step/phase progression | No hook exists for it | Not hookable today — CLI-internal, not posted to any event |

## 5. Non-goals of this audit

- No visualization/layout design (Canvas/Bezier control, node card layout) — covered separately.
- No proposal for *how* to wire `SessionStart`/`SubagentStart` into `SessionState` (a `StartedAtUtc`
  field on `SessionSnapshot`/`AgentRecord`, a new dictionary, etc.) — this is a design decision for
  the implementation plan, not an audit finding.

## 6. Concrete Design

Scope of this section: the **data model only** — how `StartedAtUtc`, `Duration` and a consumed-token
total get produced, cached, carried on the wire, and delivered to `RootsPanelNodeViewModel`. The
Canvas/Bezier node rendering is explicitly out of scope and is not touched anywhere below.

Two facts from re-reading the current source shape everything that follows, and neither is in
section 2/3 above:

1. **`AgentRecord` does not store the agent's transcript path.** `MetricsPipeline.HandleSubagentStop`
   reads `agent_transcript_path` off the payload, uses it for `TranscriptReader.TryReadLastAssistantEntry`
   / `MetaJsonReader.TryRead`, and then throws it away — it is not a field on `AgentRecord`
   (`Metrics/SessionState.cs:45-59`). So "read the agent transcript's head" has no path to read from
   unless one is added or derived.
2. **Only `AgentStatus.Live` agents ever reach the tree.** `RootsTreeBuilder.Build` filters
   `state.GetAllAgents().Where(a => a.Status == AgentStatus.Live)` (`Metrics/RootsTreeBuilder.cs:147-150`)
   and `AttachAgents` only nests agents under `session.IsLive` sessions. Live agent records come
   exclusively from `MetricsPipeline.HandleSubagentStatusLine` (whose `tasks[]` entries carry **no**
   transcript path) — the `HandleSubagentStop` path immediately calls `MarkAgentEnded`, so an agent
   whose start time is only learned at SubagentStop is, by construction, already invisible to the
   tree. **Therefore the agent start time must be obtainable without a SubagentStop payload.** This
   is the single constraint that decides 6.3.

### 6.1 Where `StartedAtUtc` lives

**Sessions: nowhere in `SessionState`. Derived at `RootsTreeBuilder` time from the permanent head
cache.** No new field on `SessionSnapshot`.

Rationale: `SessionSnapshot` is fully replaced on every `statusLine` tick
(`SessionState.UpdateSessionSnapshot` does `_sessions[snapshot.SessionId] = snapshot`,
`Metrics/SessionState.cs:96`), so any field populated per-update is exactly the "loses the original
start time on the second update" hazard. The transcript's first-line timestamp is **immutable per
file**, identical in nature to `Cwd`, and `RootsTreeBuilder._headCache` is already the permanent,
never-invalidated, path-keyed cache for exactly that class of value
(`Metrics/RootsTreeBuilder.cs:34`, and its "cache them permanently, never invalidate/re-read"
comment). Carrying it there makes overwrite-loss structurally impossible and needs zero changes to
`SessionState`, `MetricsPipeline`, or `EventServer`.

**Agents: a new nullable field on `AgentRecord`, protected by a merge in `SessionState`.** Live
agents are keyed by `agent_id` in a store whose records *are* replaced wholesale
(`SessionState.UpdateAgentRecord`, `Metrics/SessionState.cs:108`) and there is no per-agent file
path the builder can cache against without deriving one, so the record is the only durable home.
`AgentRecord` gains three fields, all appended with defaults so every existing construction site
(`MetricsPipeline.cs:54`, `MetricsPipeline.cs:237`, `SessionState.MarkAgentEnded`'s placeholder at
`SessionState.cs:127`, and `SessionStateTests`' 12 constructions) keeps compiling unchanged:

```csharp
public sealed record AgentRecord(
    ...existing 13 positional params..., 
    string? Name = null,
    DateTime? StartedAtUtc = null,       // NEW - never overwritten once non-null (see below)
    string? StartedAtSource = null,      // NEW - "transcript" | "task_start_time" | "first_seen"
    string? TranscriptPath = null);      // NEW - agent_transcript_path when SubagentStop supplied one
```

The overwrite hazard is closed **inside `SessionState`, not at the call sites** — one place, so no
future caller can regress it. `UpdateAgentRecord` stops being a raw indexer assignment and becomes:

```csharp
_agents.AddOrUpdate(
    record.AgentId,
    record,
    (_, existing) => record with
    {
        // Start time is a "first writer wins, earliest wins" value: a later update carrying null
        // (subagentStatusLine tick) must never erase it, and a later update carrying a LATER
        // timestamp must never push it forward.
        StartedAtUtc = EarliestOrExisting(existing.StartedAtUtc, record.StartedAtUtc),
        StartedAtSource = ...source of whichever timestamp won...,
        TranscriptPath = record.TranscriptPath ?? existing.TranscriptPath,
    });
```

`MarkAgentEnded` and `ReconcileLiveAgents` already use `existing with { ... }`, so they preserve the
new fields for free. This is the same "never overwrite a known value with null" rule already applied
to `ParentSessionId` in `HandleSubagentStatusLine` (GAP B, `MetricsPipeline.cs:196-201`) and to
`AgentType`/`Name`/`ModelId` in the same method — a familiar pattern, not a new concept.

`SessionSnapshot` is **not** modified. `SessionStart`/`SubagentStart` in `EventServer.HandleEventAsync`
are **deliberately left unwired** (section 2's conclusion: source (2) strictly dominates source (1)),
so `Server/EventServer.cs` is untouched by this design.

### 6.2 Reading the transcript's first-line timestamp

Extend the existing head reader; do not add a second reader or a second I/O path.

**Shape.** `TranscriptHeadInfo` gains a third, defaulted positional field
(`Metrics/TranscriptHeadReader.cs:11`):

```csharp
public sealed record TranscriptHeadInfo(
    string? Cwd,
    string? FirstUserMessageText,
    DateTime? FirstTimestampUtc = null);
```

The default keeps the four existing `new TranscriptHeadInfo(null, null)` degradation returns
(`TranscriptHeadReader.cs:71, 88, 111, 117`) and `TranscriptHeadReaderTests`' assertions compiling
untouched.

**Where.** Inside `TranscriptHeadReader.Read`'s single existing forward scan over `rawLines`
(`TranscriptHeadReader.cs:135-172`), in the same `using (doc)` block that already harvests `cwd`,
add a third guarded probe alongside it:

```csharp
if (firstTimestampUtc is null
    && root.TryGetProperty("timestamp", out var tsProp)
    && tsProp.ValueKind == JsonValueKind.String
    && TryParseIso8601Utc(tsProp.GetString(), out DateTime parsed))
{
    firstTimestampUtc = parsed;
}
```

with a private `static bool TryParseIso8601Utc(string?, out DateTime)` using
`DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal, out …)`
and a sanity gate rejecting anything outside `[2020-01-01, now + 1 day]` (a clock-skew / junk-line
guard; out-of-range degrades to "no timestamp", never throws). "**First parseable timestamp wins**",
scanning forward — not "the first line's timestamp" — for the exact reason the `cwd` probe scans
forward (`TranscriptHeadReader.cs:18-21`: the first line on this machine is a mode-marker and may
lack the field). Zero extra file opens: same buffer, same loop, same parsed `JsonDocument`.

**Caching (sessions).** `RootsTreeBuilder.GetHeadInfoCached` (`RootsTreeBuilder.cs:396-412`) needs no
structural change — it caches the whole `TranscriptHeadInfo`, so the timestamp rides along. One
subtlety worth pinning in a comment and a test: the cache-admission condition is `info.Cwd is not null`
(`RootsTreeBuilder.cs:405`), so a head window that yields a timestamp but no `cwd` is not cached and
is re-read next tick. That is **correct and compatible** — the value is immutable in-file, so a
re-read returns the same timestamp; the only cost is the re-read that already happens today for that
same case. Do **not** relax the admission condition to `Cwd is not null || FirstTimestampUtc is not null`:
that would permanently cache a null `cwd` for a still-being-written file and reintroduce the
"unattributed forever" bug that comment guards against.

### 6.3 Sub-agents specifically

Per the constraint established above, the tree only ever renders **live** agents, and live agent
records arrive from `subagentStatusLine` without a transcript path. Start time for an agent is
therefore resolved by a **three-tier tolerant ladder**, mirroring the existing five-tier name
resolution ladder in `BuildSessionDto` (`RootsTreeBuilder.cs:226-268`) and recorded in
`AgentRecord.StartedAtSource` exactly the way `SessionTreeDto.NameSource` records which name tier
won:

| Tier | Source | `StartedAtSource` | Where it is applied |
|---|---|---|---|
| 1 | Head-window `timestamp` of `agent-<id>.jsonl` | `"transcript"` | `RootsTreeBuilder.ToAgentDto` (path by convention or `record.TranscriptPath`) |
| 2 | `tasks[].startTime` on the `subagentStatusLine` payload | `"task_start_time"` | `MetricsPipeline.HandleSubagentStatusLine` |
| 3 | Earliest observed `ReceivedAtUtc` for that `agent_id` | `"first_seen"` | `SessionState.UpdateAgentRecord`'s merge |

Tier 2 is essentially free and should be implemented: the realistic `tasks[]` fixture in
`tests/accel.Tests/SubagentStatusLinePrintingTests.cs:61` already contains
`"startTime": "2026-08-13T10:00:00Z"` on a task entry, and nothing parses it today. Treat it as
**optional and unverified against a live payload** — parse it with the same
`TryParseIso8601Utc` helper via a new tolerant `GetTaskDateTime(task, "startTime")` sibling of the
existing `GetTaskInt`/`GetTaskEffort`/`GetTaskModelId` helpers (`MetricsPipeline.cs:271-321`); absent
or wrong-typed simply yields null and falls through to tier 3.

Tier 1, the head read, needs a path. **Do not add a per-tick directory scan.** The path is derivable
by convention from data `RootsTreeBuilder` already holds at `AttachAgents` time
(`RootsTreeBuilder.cs:188-206`): the enclosing `SessionTreeDto` supplies `ProjectDir` (the slug) and
`SessionId`, and `Build` already knows `projectsDir`:

```
<projectsDir>\<session.ProjectDir>\<session.SessionId>\subagents\agent-<record.AgentId>.jsonl
```

`record.TranscriptPath` (populated by `HandleSubagentStop`, tier-1 exact) takes precedence over the
convention path when present. `AttachAgents` and `ToAgentDto` gain a `string? subagentsDir` parameter
threaded from `Build`; `ToAgentDto` is also called for unattributed agents
(`RootsTreeBuilder.cs:166`) where no owning session exists — pass `null` there, and those agents
simply resolve start time at tier 2/3.

**Read cost control.** A new per-builder cache, sibling to `_headCache`/`_tailCache`:

```csharp
private sealed record AgentStartCacheEntry(DateTime? StartedAtUtc, DateTime LastAttemptUtc);
private readonly ConcurrentDictionary<string, AgentStartCacheEntry> _agentStartCache = new(StringComparer.Ordinal);
public int AgentStartCacheCount => _agentStartCache.Count;   // test hook, matching HeadCacheCount/TailCacheCount
```

Keyed by **`agent_id`, not path** (an agent id is globally unique and the value is immutable, so this
never needs invalidation and survives a path-derivation change). A hit with a non-null
`StartedAtUtc` is permanent. A miss (file not yet created — the common race: the agent appears in
`tasks[]` before its transcript exists) records `LastAttemptUtc` and is **retried at most once every
10 seconds**, so a live agent whose file never materializes costs one bounded 64 KB read per 10 s
rather than one per ~2 s telemetry tick. The read itself reuses `TranscriptHeadReader.Read` verbatim
— the sub-agent transcript is the same JSONL shape (section 2), so no sub-agent-specific reader is
needed; only `FirstTimestampUtc` is consumed from the result and `Cwd`/`FirstUserMessageText` are
ignored.

### 6.4 "Consumed tokens" — definition, name, layer

**Definition, agents:** `ConsumedTokens = InputTokens + OutputTokens + CacheCreationInputTokens + CacheReadInputTokens`,
computed as `long` (four `int`s can overflow `int` on a long-running agent).

**Definition, sessions:** `ConsumedTokens = UsedTokens` (i.e. input + cache_creation + cache_read,
**no output tokens** — `statusLine`'s `context_window.current_usage` has no output field, per
`MetricsPipeline.HandleStatusLine:125-132`), accompanied by a boolean
`ConsumedTokensIsContextOnly = true` so the UI can render the caveat instead of silently presenting
a session total as comparable to an agent total (section 3's closing paragraph). Note the historical
(non-live) session path computes the same input+cache sum from the transcript tail
(`RootsTreeBuilder.cs:323`) and is likewise output-less, so the flag is `true` for **every** session
row regardless of path — it is a constant of the session data source, not a per-row condition. Keep
it as a real field anyway (rather than hard-coding `true` in the UI) so the day a `statusLine`
version adds an output count, exactly one line in `BuildSessionDto` changes.

**Layer:** computed **once, in `RootsTreeBuilder`** (`ToAgentDto` for agents, `BuildSessionDto` for
sessions) and materialized into the DTOs — the same place `UsedPercentage` is already derived
(`RootsTreeBuilder.cs:305, 325, 368`). Not a property on `AgentRecord` (that record is the raw
event-sourced shape and should stay raw), not a computation in `MonitorTreeBuilder` (which is a pure
string projection and must not own arithmetic definitions), and explicitly **not** folded into
`UsedTokens`/`UsedPercentage`, which must keep their current input-side-only meaning for the context
gauge to stay correct.

Naming is deliberately `ConsumedTokens` / `consumed_tokens` — never "total tokens", never "used
tokens" — so it is impossible to confuse with `UsedTokens` at a call site or in the JSON.

### 6.5 `Duration` — shape and where it is computed

**Materialized as `DurationMs` (`long?`) into the DTOs at `RootsTreeBuilder` time, not a property
computed per layer.**

Reasons, all concrete: (a) `RootsTreeBuilder` is the only layer that knows `IsLive` *and*
`LastActivityUtc` (`BuildSessionDto` sets both, `RootsTreeBuilder.cs:281-332`); (b) a `DateTime.UtcNow`-based
property would make every DTO non-deterministic and impossible to assert on in
`MonitorTreeBuilderTests`/`RootsTreeRouteTests`, which compare fixture DTOs directly; (c) the wire
document is snapshot-shaped ("rendered as of T", `SessionSnapshot`'s doc comment) and a live-ticking
property would violate that contract; (d) `RootsTreeDto.GeneratedAtUtc` already exists as the
snapshot's single "now".

Definition:

```
end      = IsLive ? nowUtc : LastActivityUtc
DurationMs = StartedAtUtc is null ? null : (long)Math.Max(0, (end - StartedAtUtc.Value).TotalMilliseconds)
```

where `nowUtc` is **one `DateTime.UtcNow` captured once at the top of `RootsTreeBuilder.Build`** and
reused for every row plus `GeneratedAtUtc` (`RootsTreeBuilder.cs:184` currently calls `UtcNow` a
second time there — fold both onto the single captured value), so no two rows in the same document
are measured against different clocks. `Math.Max(0, …)` clamps the clock-skew case
(`LastActivityUtc` = file mtime can legitimately precede a transcript timestamp on a machine whose
clock moved) rather than rendering a negative duration.

Agents use the same formula with `end = AsOf` (`record.ReceivedAtUtc`) for non-live agents; since only
live agents currently reach the tree, in practice the agent branch is always `nowUtc` today — write
the general form anyway so it stays correct if the "live only" filter at `RootsTreeBuilder.cs:147`
is ever relaxed.

A single shared private helper on `RootsTreeBuilder` implements it once for both:
`private static long? ComputeDurationMs(DateTime? startedAtUtc, bool isLive, DateTime endUtc, DateTime nowUtc)`.

### 6.6 Wire format and column plumbing

**`SessionTreeDto`** (`Metrics/RootsTreeBuilder.cs:589-607`) gains four fields, appended after
`LastActivityUtc` and **before** `Agents` is not an option (positional record) — append them at the
very end with defaults so the six fixture constructions in `MonitorTreeBuilderTests` and
`RootsTreeRouteTests` keep compiling:

```csharp
[property: JsonPropertyName("started_at_utc")] DateTime? StartedAtUtc = null,
[property: JsonPropertyName("started_at_source")] string? StartedAtSource = null,
[property: JsonPropertyName("duration_ms")] long? DurationMs = null,
[property: JsonPropertyName("consumed_tokens")] long? ConsumedTokens = null,
[property: JsonPropertyName("consumed_tokens_is_context_only")] bool ConsumedTokensIsContextOnly = false
```

**`AgentTreeDto`** (`RootsTreeBuilder.cs:610-625`) gains four, same append-with-defaults rule:

```csharp
[property: JsonPropertyName("started_at_utc")] DateTime? StartedAtUtc = null,
[property: JsonPropertyName("started_at_source")] string? StartedAtSource = null,
[property: JsonPropertyName("duration_ms")] long? DurationMs = null,
[property: JsonPropertyName("consumed_tokens")] long? ConsumedTokens = null
```

(no `consumed_tokens_is_context_only` on agents: an agent total genuinely includes output tokens).
All new fields are nullable/defaulted, so an older consumer that ignores them, or a document
serialized before the fields existed, round-trips unchanged.

**`MonitorRowColumns`** (`Cli/MonitorTreeBuilder.cs:24-27`) keeps its exact six-positional shape and
gains two **appended, defaulted** members, so every existing 6-argument construction
(`MonitorTreeBuilder.cs:75, 94, 119, 154`, `RootsPanelViewModel.cs:696`) and `MonitorRowColumns.Empty`
compile and behave identically, and the six existing column assertions in `MonitorTreeBuilderTests`
(`Build_SessionColumns_MapExpectedFields` et al.) still pass untouched:

```csharp
public sealed record MonitorRowColumns(
    string Id, string Name, string Type, string Model, string Effort, string Context,
    string Duration = "", string Tokens = "");
```

Both new columns are **display strings**, consistent with every other member of this record (`Context`
is already a pre-formatted `"14.8% of 1M (assumed)"` string). Two new public pure formatters live
next to the existing private `FormatWindowSize`/`FormatPercentage` in `MonitorTreeBuilder`, made
`public static` like `GlyphFor` so the future graph control reuses them rather than re-deriving:

- `public static string FormatDuration(long? ms)` — `null` → `"—"` (the existing `EmDash` const,
  `MonitorTreeBuilder.cs:60`); `< 60 s` → `"12s"`; `< 60 min` → `"7m 04s"`; else `"1h 23m"`.
  `CultureInfo.InvariantCulture` throughout, matching `FormatWindowSize`.
- `public static string FormatTokenCount(long? tokens)` — `null` → `"—"`; `< 1000` → `"842"`;
  `< 1_000_000` → `"148.2K"`; else `"1.4M"`. Deliberately *not* reusing `FormatWindowSize`, whose
  exact-multiple-only rule (`v % 1_000_000 == 0`) is right for round window sizes and useless for
  arbitrary token counts.

**Raw numbers must also survive**, because the graph will want to scale bars/edges, not parse
`"1h 23m"` back. `MonitorSessionNode` and `MonitorAgentNode` (`MonitorTreeBuilder.cs:30-37`) each gain
two appended defaulted fields carrying the unformatted values through:

```csharp
public sealed record MonitorAgentNode(string AgentId, string Text, MonitorNodeState State, MonitorRowColumns Columns,
    long? DurationMs = null, long? ConsumedTokens = null);
public sealed record MonitorSessionNode(string SessionId, string Text, MonitorNodeState State, MonitorAgentNode[] Agents,
    MonitorRowColumns Columns, string ProjectDir = "",
    long? DurationMs = null, long? ConsumedTokens = null);
```

(`MonitorSessionNode`'s new params go after the existing defaulted `ProjectDir`, preserving its
positional meaning.)

**`RootsPanelNodeViewModel`** (`App/ViewModels/RootsPanelViewModel.cs:44-75`) gains two optional
constructor parameters after `projectDir`, and two get-only properties:

```csharp
public long? DurationMs { get; }
public long? ConsumedTokens { get; }
```

plus `public string DurationText => Columns.Duration;` / `public string TokensText => Columns.Tokens;`
for binding symmetry with the existing `Columns.*`-derived surface. `BuildSessionNode`/`BuildAgentNode`
(`RootsPanelViewModel.cs:715-728`) pass them through; `BuildRootNode`'s and the placeholder's
constructions stay 6-column and simply leave both null. `BuildTooltipText` (`RootsPanelViewModel.cs:221-235`)
appends `" — {Columns.Duration}"` and `" — {Columns.Tokens}"` only when non-empty, preserving the
exact existing tooltip string for rows without the new data (which is what
`RootsPanelViewModelTests:913-914` asserts against).

### 6.7 Degraded-data behaviour

Every new value is nullable end to end and every layer degrades the way the rest of this codebase
already does — "fewer results, never an exception" (`RootsTreeBuilder.Build`'s catch blocks),
"every field optional… must never throw" (`TranscriptHeadReader`/`TranscriptReader` class docs).

| Situation | `StartedAtUtc` | `DurationMs` | Rendered |
|---|---|---|---|
| Live session, head read still racing Claude Code's writer | `null` | `null` | `"—"` in the Duration column; row otherwise unchanged |
| Agent visible in `tasks[]` before its `agent-<id>.jsonl` exists | `null` (tier 1 miss) → tier 2/3 if available | from whichever tier won | never blank-crashes; `started_at_source` says which tier |
| Transcript line has a malformed / non-string / out-of-range `timestamp` | `null` | `null` | `"—"` |
| Locked / unreadable transcript file | `null` (existing `catch` in `TranscriptHeadReader.Read`) | `null` | `"—"` |
| Clock skew: `LastActivityUtc` < `StartedAtUtc` | set | `0` (clamped) | `"0s"`, never a negative |
| Session/agent with no token data at all | — | — | `ConsumedTokens = null` → `"—"`, distinct from a real `0` |
| Old serialized `/roots/tree` document without the new fields | `null` via record defaults | `null` | `"—"` |

Hard rules: no new `throw`, no new `!` dereference, no new `Parse` without `TryParse`, and no new
exception filter that could swallow a real bug silently outside the existing best-effort I/O
boundaries.

Two caveats to carry into the graph work (not defects, but they will be visible):

- **Ended agents have no row at all**, so their final duration is never rendered today (see the
  constraint at the top of section 6). Surfacing finished agents is a separate change to the
  `Status == AgentStatus.Live` filter at `RootsTreeBuilder.cs:148` and is out of scope here.
- **A session's `ConsumedTokens` is not comparable to an agent's** (no output tokens) — hence
  `consumed_tokens_is_context_only`; the graph must render the caveat, not average the two.

### 6.8 Test-impact map

**Existing files needing new fixture fields or assertions:**

- `tests/accel.Tests/TranscriptHeadReaderTests.cs` — its `ModeLine`/`UserStringLine`/`UserArrayLine`
  helpers (lines 26-70) gain an optional `timestamp` parameter. New cases:
  `Read_FirstLineWithTimestamp_ReturnsItAsFirstTimestampUtc`,
  `Read_FirstLineWithoutTimestampSecondWithOne_ReturnsTheSecond` (the mode-marker case),
  `Read_MalformedTimestampString_YieldsNullNotThrow`,
  `Read_TimestampOutsideSanityRange_YieldsNull`,
  `Read_NonUtcOffsetTimestamp_IsNormalizedToUtc`.
- `tests/accel.Tests/MetricsPipelineTests.cs` — `SubagentStop_WithAgentTranscriptPath_PopulatesEndedAgentRecord`
  (line 79) extends its transcript fixture with a `timestamp` and asserts `record.TranscriptPath` and
  `record.StartedAtUtc`. New: `SubagentStatusLine_TaskWithStartTime_PopulatesStartedAtUtcFromTask`,
  `SubagentStatusLine_TaskWithoutStartTime_LeavesStartedAtUtcNull`,
  `SubagentStatusLine_MalformedStartTime_IsIgnoredAndDoesNotBreakTheBatch`.
- `tests/accel.Tests/SessionStateTests.cs` — its 12 `new AgentRecord(...)` constructions keep
  compiling via the defaulted params; new cases for the merge rule:
  `UpdateAgentRecord_LaterUpdateWithNullStartedAt_KeepsTheOriginal`,
  `UpdateAgentRecord_LaterUpdateWithLaterStartedAt_KeepsTheEarlier`,
  `UpdateAgentRecord_LaterUpdateWithNullTranscriptPath_KeepsTheKnownPath`,
  `MarkAgentEnded_PreservesStartedAtUtc`, `ReconcileLiveAgents_StalePathPreservesStartedAtUtc`.
- `tests/accel.Tests/MonitorTreeBuilderTests.cs` — `LiveSession`/`HistoricalSession`/`LiveAgent`/
  `StaleAgent`/`HistoricalAgent` (lines 16-105) gain the new DTO args. New cases:
  `Build_SessionColumns_DurationAndTokensAreFormatted`,
  `Build_AgentColumns_DurationAndTokensAreFormatted`,
  `Build_NullDurationAndTokens_RenderEmDash`,
  `Build_SessionNode_CarriesRawDurationMsAndConsumedTokens`,
  plus `FormatDuration`/`FormatTokenCount` `[Theory]` tables (seconds/minutes/hours; 842 / 148.2K / 1.4M / null).
- `tests/accel.Tests/RootsTreeRouteTests.cs` — its `WriteSessionFile`/`ModeLine`/`UserLine` helpers
  (lines 67-72 and friends) gain timestamps. New cases:
  `RootsTree_SessionWithTranscriptTimestamp_ExposesStartedAtUtcAndDurationMs`,
  `RootsTree_SessionWithoutAnyTimestamp_ExposesNullStartedAtAndNullDuration`,
  `RootsTree_LiveSession_DurationIsMeasuredAgainstGeneratedAtUtc`,
  `RootsTree_HistoricalSession_DurationIsMeasuredAgainstLastActivity`,
  `RootsTree_SessionConsumedTokens_IsFlaggedContextOnly`,
  and the JSON-key assertions for `started_at_utc` / `started_at_source` / `duration_ms` /
  `consumed_tokens` / `consumed_tokens_is_context_only`.
- `tests/accel.Tests/RootsPanelViewModelTests.cs` — new:
  `Rebuild_SessionNode_ExposesDurationAndConsumedTokens`,
  `TooltipText_WithDurationAndTokens_AppendsThem`,
  `TooltipText_WithoutDurationAndTokens_IsUnchanged` (pins backward compatibility of the existing
  tooltip format).

**New test files:**

- `tests/accel.Tests/RootsTreeBuilderStartTimeTests.cs` — the head/agent-start caching contract
  against a fixture `projectsDirOverride` tree, driving `RootsTreeBuilder` directly (no HTTP), in the
  style of `RootsTreeRouteTests`' fixture writers: `HeadCache_TimestampIsReusedAcrossBuilds`,
  `AgentStart_ResolvedFromConventionSubagentsPath`,
  `AgentStart_PrefersRecordTranscriptPathOverConventionPath`,
  `AgentStart_MissIsRetriedNoMoreThanOncePerTenSeconds` (asserts via `AgentStartCacheCount` and a
  file created between builds), `AgentStart_UnattributedAgentWithNoSessionDir_DegradesToNull`.
- `tests/accel.Tests/DurationAndTokenFormattingTests.cs` — only if the formatters are moved out of
  `MonitorTreeBuilder` into their own type; if they stay as `public static` members of
  `MonitorTreeBuilder` (the recommendation), keep those `[Theory]`s in `MonitorTreeBuilderTests` and
  do not create this file.

No smoke test is needed: nothing here touches real process/PTY/UI lifecycle, which is the stated
boundary in `CLAUDE_DESIGN.md` §3.

### 6.9 Implementation order

Each step is independently committable, compiles on its own, and leaves the app working.

1. **`TranscriptHeadInfo.FirstTimestampUtc` + `TryParseIso8601Utc` in `TranscriptHeadReader`.**
   Reader-only change, no consumers yet. Tests: new `TranscriptHeadReaderTests` cases (6.8).
2. **`AgentRecord.StartedAtUtc` / `StartedAtSource` / `TranscriptPath` + the merge in
   `SessionState.UpdateAgentRecord`.** Fields written by nobody yet; merge semantics tested in
   isolation. Tests: new `SessionStateTests` cases.
3. **Populate the agent fields in `MetricsPipeline`**: `TranscriptPath` + tier-1 head timestamp in
   `HandleSubagentStop`; tier-2 `GetTaskDateTime(task, "startTime")` in `HandleSubagentStatusLine`.
   Tests: new `MetricsPipelineTests` cases.
4. **`SessionTreeDto` / `AgentTreeDto` wire fields** (all defaulted-null), populated with `null` only —
   pure additive schema step, keeps the JSON contract change isolated from the logic that fills it.
   Tests: `RootsTreeRouteTests` key-presence assertions.
5. **`RootsTreeBuilder`: single captured `nowUtc`, `ComputeDurationMs`, session `StartedAtUtc` from
   the head cache, session/agent `ConsumedTokens`.** First step where real numbers appear on the
   wire for sessions. Tests: `RootsTreeRouteTests` session cases.
6. **`RootsTreeBuilder`: `_agentStartCache`, the convention subagents path, `subagentsDir` threaded
   through `AttachAgents`/`ToAgentDto`, tier-1 agent start.** Tests: new
   `RootsTreeBuilderStartTimeTests.cs`.
7. **`MonitorTreeBuilder`: `MonitorRowColumns.Duration`/`Tokens`, `FormatDuration`/`FormatTokenCount`,
   raw `DurationMs`/`ConsumedTokens` on `MonitorSessionNode`/`MonitorAgentNode`.** Tests: new
   `MonitorTreeBuilderTests` cases + formatter theories.
8. **`RootsPanelNodeViewModel`: `DurationMs`/`ConsumedTokens`/`DurationText`/`TokensText`, tooltip
   append, pass-through in `BuildSessionNode`/`BuildAgentNode`.** Tests: new
   `RootsPanelViewModelTests` cases. **Panel E is now fully fed** — everything after this is
   visualization, which is out of scope here.
9. **Doc pass**: update `CLAUDE_ARCHITECTURE.md`'s data-flow description of `/roots/tree` with the
   four/five new fields and the three-tier agent start ladder, and mark section 4's gap table above
   as closed for Duration and Tokens.

## 7. Frontend Visualization Design

Scope of this section: **Panel E only** — the control, view models, layout math and wiring that render
the data model section 6 already delivers. Section 6's output types (`MonitorRowColumns.Duration`/
`.Tokens`, `MonitorSessionNode`/`MonitorAgentNode`'s `DurationMs`/`ConsumedTokens`,
`RootsPanelNodeViewModel.DurationText`/`TokensText`) are treated as **fixed, given interfaces**; nothing
below changes `Metrics/`, `Cli/MonitorTreeBuilder.cs`, `Server/`, or `RootsPanelViewModel`.

The requirement being satisfied, verbatim: *"clear visualization of the current context with the running
sub-agent children. Each element (parent or child) must show the Model, Effort, Context information,
execution duration and consumed tokens. Modern tree visualization with bezier curved parent/child
connectors."*

Current state being replaced: `App/MainWindow.xaml:626-637` — a `Border x:Name="PanelE"` containing a
`StackPanel` with a `"AGENT GRAPH"` `SectionHeaderTextStyle` header and one `{Binding StatusText}`
`TextBlock`, whose `DataContext` is a `FocusedSessionStubViewModel` constructed at
`App/MainWindow.xaml.cs:118` and disposed at `:130`.

Three facts from re-reading the current source shape everything below:

1. **Panel E's grid row is short and wide**, not tall: `MainWindow.xaml:506` is
   `<RowDefinition Height="1*" MinHeight="64" MaxHeight="160" />` inside the center column. Any vertical
   tree layout is wrong for this aspect ratio by construction — see 7.2.
2. **Panel A's node view models are thrown away and rebuilt wholesale on every telemetry tick**
   (`RootsPanelViewModel.Rebuild` does `Roots.Clear()` at `RootsPanelViewModel.cs:586` then re-adds fresh
   `RootsPanelNodeViewModel` instances). Any panel-E design that holds a reference into panel A's tree
   holds a reference that is stale ~250 ms later. This decides 7.1.
3. **`MonitorTreeBuilder.BuildSessionNode`/`BuildAgentNode` are `private`** (`Cli/MonitorTreeBuilder.cs:116, 143`);
   the only public entry point is `MonitorTreeBuilder.Build(RootsTreeDto?)`. So a panel that wants the
   *formatted* columns for one session must build the whole `MonitorTree` and then select from it — it
   cannot ask for one session's projection. This decides the shape of `AgentGraphViewModel.Rebuild`.

### 7.1 Scope of "current context" — which ViewModel Panel E binds to

**Decision: a new `AgentGraphViewModel` (`App/ViewModels/AgentGraphViewModel.cs`), fed by the same
`ITelemetryFeed` instance panel A uses plus the read-only `ISessionSelectionService`. Panel E does not
reference `RootsPanelViewModel` in any form.**

The focused session is the root/parent node; its `MonitorSessionNode.Agents` are the children. This does
match how the data already flows: `RootsTreeBuilder.AttachAgents` nests live agents under their
`ParentSessionId` session, `MonitorTreeBuilder.BuildSessionNode` (`Cli/MonitorTreeBuilder.cs:136`) carries
them into `MonitorSessionNode.Agents`, and `RootsPanelViewModel.BuildSessionNode`
(`RootsPanelViewModel.cs:753-765`) already renders exactly that parent/child pair in panel A. Panel E
renders the *same* parent/child pair for one session, in a different visual form.

**Why not reuse `RootsPanelViewModel`'s already-built tree, filtered to the focused session's subtree:**

- It is precisely the cross-panel binding this codebase forbids. `MainWindow.xaml.cs:96-99` states the
  rule inline — *"Scoped to panel A only - deliberately not `Window.DataContext`, so the remaining
  placeholder panels can't accidentally start binding against panel A's ViewModel (locked-in decision 8:
  no point-to-point panel bindings)"* — and `CLAUDE_ARCHITECTURE.md` §2.7 repeats it ("Each panel is bound
  individually … never `Window.DataContext` — so panels can't accidentally cross-bind").
- Fact 2 above: panel A's nodes are replaced on every rebuild, so panel E would have to re-find its
  subtree by key after every one of panel A's rebuilds — i.e. re-implement the projection anyway, on top
  of a dependency on panel A's rebuild ordering.
- Panel A's node objects carry panel-A-only mutable state (`IsExpanded`, `IsSelected`, and the
  `_owner`-callback into `RootsPanelViewModel.OnNodeSelectionChanged`, `RootsPanelViewModel.cs:224`).
  Sharing them would let a panel-E interaction mutate panel A's tree selection.

**Why this does not violate "single writer, many readers" (`CLAUDE_ARCHITECTURE.md` §2.7/§4):**

- Panel E is a **reader** of `ISessionSelectionService`, exactly like panel A (`RootsPanelViewModel.cs:356`)
  and exactly like the `FocusedSessionStubViewModel` it replaces. It never touches `ISessionSelectionWriter`;
  `TabsViewModel` remains the sole writer, structurally (`SessionSelectionService.AcquireWriter` throws on
  a second call, `ISessionSelectionService.cs:120-135`).
- Panel E never touches `SessionState`, `SessionState.Changed`, a `FileSystemWatcher`, a timer, or
  `HttpClient` — it consumes whole `RootsTreeDto` snapshots from `ITelemetryFeed`, the same contract
  `RootsPanelViewModel`'s class doc pins down ("No direct event wiring", `RootsPanelViewModel.cs:283-288`).
  Two readers on one feed is the feed's designed shape (`ITelemetryFeed.SnapshotAvailable` is a
  multicast `event Action<RootsTreeDto>`, `App/Services/ITelemetryFeed.cs:27`), and both panels therefore
  render the same snapshot by construction, the same guarantee `EventServerTelemetrySource` gives the UI
  vs. the `/roots/tree` route.

**Projection, exactly:**

```csharp
public void Rebuild(RootsTreeDto? snapshot)      // public so tests drive it directly, like RootsPanelViewModel.Rebuild
{
    _latest = snapshot;
    var tree = MonitorTreeBuilder.Build(snapshot);        // fact 3: only public entry point
    var session = FindFocusedSession(tree);               // null when nothing focused / not in this snapshot
    ProjectNodes(session);
}

private MonitorSessionNode? FindFocusedSession(MonitorTree tree)
{
    if (_selection?.FocusedSessionId is not { Length: > 0 })
    {
        return null;
    }

    return EnumerateSessions(tree).FirstOrDefault(s => _selection.IsFocused(s.SessionId));
}

private static IEnumerable<MonitorSessionNode> EnumerateSessions(MonitorTree tree) =>
    tree.Roots.SelectMany(r => r.Sessions).Concat(tree.Unattributed?.Sessions ?? Array.Empty<MonitorSessionNode>());
```

`ISessionSelectionService.IsFocused` is used rather than a hand-rolled string compare, because it already
owns the case-insensitivity rule and its rationale (`ISessionSelectionService.cs:47-50`: a tabId GUID and
a transcript-derived session id need not agree on hex casing).

**Cost note, stated deliberately:** this calls `MonitorTreeBuilder.Build` a second time per tick (panel A
does the first). `MonitorTreeBuilder` is pure, allocation-only, in-memory work over an
already-materialized DTO — no I/O, no `RootsTreeBuilder` re-scan — and it is the price of not
cross-binding to panel A. If it ever measures as a problem, the fix is to make
`MonitorTreeBuilder.BuildSessionNode` public, not to share panel A's view models.

**Change signals** (both marshalled through `IUiThreadDispatcher.Post`, same discipline as
`RootsPanelViewModel.OnSnapshotAvailable`/`OnFocusedSessionChanged`, `RootsPanelViewModel.cs:526, 540`):

| Signal | Handler | Work done |
|---|---|---|
| `ITelemetryFeed.SnapshotAvailable` | `OnSnapshotAvailable` | full `Rebuild(snapshot)` |
| `ITelemetryFeed.SnapshotFailed` | `OnSnapshotFailed` | `StatusText = $"Refresh failed: {message}"`, tree left as-is (verbatim `RootsPanelViewModel.cs:561-567`) |
| `FocusedSessionChangedMessage` | `OnFocusedSessionChanged` | `Rebuild(_latest)` — a focus change with no new telemetry must still re-target the graph, and re-projecting the cached snapshot is the whole cost |

Constructor, mirroring `RootsPanelViewModel`'s (interfaces + test seams, no DI container):

```csharp
public AgentGraphViewModel(
    ITelemetryFeed feed,
    IUiThreadDispatcher dispatcher,
    ISessionSelectionService? selection = null)
```

with the same `if (_feed.Latest is { } latest) Rebuild(latest);` catch-up in the constructor body
(`RootsPanelViewModel.cs:360-363`) so a panel constructed after `Start()` is never blank, and the same
`Dispose()` shape (unhook both feed events, `_selection?.Unsubscribe(this)`).

### 7.2 Tree layout algorithm

**Decision: horizontal, left-to-right, column-major flow. Parent card pinned at the left edge and
vertically centred; children laid out in one or more vertical "child columns" to its right; the whole
surface horizontally scrollable.**

Rationale: fact 1 — panel E's row is `MinHeight="64" MaxHeight="160"` and spans the full width of the
centre column (typically 700-1200 px). A vertical tree (parent on top, children below) gets one usable
rank and then clips; a radial layout needs a square-ish aspect ratio it will never get here. Left-to-right
is the standard "modern tree viz" orientation for a wide viewport (and the one whose connectors read as
horizontal beziers, which is what the requirement asks for).

**Constants** (`AgentGraphLayoutOptions`, a `readonly record struct` with these defaults, so tests can
drive degenerate values without touching the control):

| Name | Default | Why |
|---|---|---|
| `CardWidth` | `176.0` | fits badge + effort ring + a trimmed name on row 1, and `"7m 04s · 148.2K · 12.3%"` on row 2 at `FontSizeCaption` |
| `CardHeight` | `52.0` | two text rows at 14/12 px + 8 px vertical padding, on the 4 px spacing grid |
| `ColumnGap` | `56.0` | the horizontal run the bezier needs to read as a curve rather than a kink |
| `RowGap` | `10.0` | ≈ `SpacingSm`, distinct from `ColumnGap` |
| `Padding` | `12.0` | ≈ `SpacingMd`, uniform |

**Geometry** (all of it in the pure `AgentGraphLayout.Compute`, see 7.7):

```
rowsPerColumn = Math.Max(1, (int)Math.Floor((height - 2*Padding + RowGap) / (CardHeight + RowGap)))
columnCount   = childCount == 0 ? 0 : (int)Math.Ceiling(childCount / (double)rowsPerColumn)

parent.X = Padding
parent.Y = Math.Max(Padding, (height - CardHeight) / 2)

for child i:
    c = i / rowsPerColumn                      // column index, 0-based
    r = i % rowsPerColumn                      // row within that column
    countInColumn = Math.Min(rowsPerColumn, childCount - c*rowsPerColumn)
    blockHeight   = countInColumn*CardHeight + (countInColumn-1)*RowGap
    columnTop     = Math.Max(Padding, (height - blockHeight) / 2)
    child.X = Padding + CardWidth + ColumnGap + c*(CardWidth + ColumnGap)
    child.Y = columnTop + r*(CardHeight + RowGap)

contentWidth  = Padding + CardWidth + columnCount*(ColumnGap + CardWidth) + Padding
contentHeight = Math.Max(height, Padding + CardHeight + Padding)
```

Column-major (fill a column top-to-bottom, then start a new column to the right) rather than row-major is
deliberate: every child stays a direct child of the same parent, all connectors still originate at the
parent's right edge, and adding an agent extends the surface **rightwards** (where there is room and a
scrollbar) instead of downwards (where there is a `MaxHeight`). Each column's block is independently
vertically centred, so a trailing partial column reads as balanced rather than top-heavy.

**Degenerate counts:**

- **0 children** — `columnCount = 0`, `contentWidth` collapses to just the parent card plus padding; the
  parent renders alone, vertically centred, with the "No sub-agents running" hint of 7.5 to its right.
- **1 child** — one column, one row; `blockHeight == CardHeight`, so `columnTop == parent.Y` and the child
  is exactly vertically aligned with the parent. The connector is a flat S-curve (start and end share a Y),
  which is the correct degenerate case, not a special case in the code.
- **many children** — at panel E's `MaxHeight="160"`: `rowsPerColumn = floor((160-24+10)/62) = 2`, so 6
  agents render as 3 columns of 2. At `MinHeight="64"`: `rowsPerColumn = 1`, a single horizontal row of
  cards — still correct, just wider.

**Resize.** `AgentGraphControl` recomputes on `SizeChanged` (height changes `rowsPerColumn`; width changes
nothing but the scroll extent) and on any change to the bound node collection. There is exactly one entry
point, `AgentGraphControl.Relayout()`, called from `SizeChanged`, from `Loaded`, and from the
`INotifyCollectionChanged` handler on `AgentGraphViewModel.Nodes` — never from three divergent code paths.

**Recommended XAML change to panel E's row** (`MainWindow.xaml:506`), so one card row always fits and two
are reachable:

```xml
<RowDefinition Height="1*" MinHeight="88" MaxHeight="220" />
```

`MinHeight="88"` = `Padding + CardHeight + Padding` (76) plus the panel's own header line; `MaxHeight="220"`
yields `rowsPerColumn = 3`. The accompanying comment at `MainWindow.xaml:497-502` ("still a Phase-6 stub
with almost nothing to show") is updated in the same edit.

### 7.3 Bezier connector rendering

**Decision: `Path` elements whose `Data` is a `PathGeometry` built in code-behind from a `BezierSegment` —
not an `OnRender` override, and not data-bound geometry.**

Justification against `EffortBarsControl`, which is this codebase's one precedent for a custom-drawn
control: it declares the invariant part in XAML (`EffortBarsControl.xaml:19-22` — `TrackRing`, `FillArc`,
`FillDisc`) and builds only the data-dependent geometry in code
(`EffortBarsControl.xaml.cs:95-111`, `BuildArcGeometry` → `PathFigure` + `ArcSegment` → `PathGeometry`
assigned to `FillArc.Data`), with the XAML comment stating exactly why: *"FillArc's geometry is entirely
code-behind … since the arc's end point/large-arc-flag depend on Level; TrackRing here is the one part that
never changes, so it stays as plain XAML."* A bezier whose four control points depend on measured node
positions is the identical situation. There is **no `OnRender` override anywhere in `App/`**, so choosing
one would be inventing a second custom-drawing idiom; and `Path` additionally keeps `StrokeStartLineCap`,
`Opacity`, hit-testing and per-element `AutomationProperties` available, all of which `OnRender` gives up.

Data-bound geometry is rejected for a concrete reason, not taste: the control points depend on both nodes'
final positions, which are known only after `AgentGraphLayout.Compute` runs against the control's measured
`ActualHeight`. Expressing that as a `MultiBinding` + converter over four coordinates would put layout
arithmetic in a converter and still need a code-behind trigger to re-evaluate on `SizeChanged` — strictly
more machinery for the same result.

**Control-point formula** (computed in the pure `AgentGraphLayout`, see 7.7, so it is unit-testable
without WPF), for a parent rect `p` and child rect `c`:

```
startX = p.X + CardWidth            startY = p.Y + CardHeight/2      // parent's right edge, vertical centre
endX   = c.X                        endY   = c.Y + CardHeight/2      // child's left edge, vertical centre
dx     = endX - startX
k      = Math.Clamp(dx * 0.5, 24.0, 96.0)
c1     = (startX + k, startY)       c2 = (endX - k, endY)
```

Both control points share their anchor's Y, so the curve leaves the parent horizontally and enters the
child horizontally — the "modern"/dendrogram look, and the reason a same-Y pair (the 1-child case)
degenerates to a straight horizontal line rather than a bulge. `k` is clamped: the lower bound keeps a
short hop curved, the upper bound stops a far-right column's edge (`dx` up to ~460 px at 3 columns) from
ballooning into a flat, unreadable arc.

**Rendering.** `AgentGraphControl.Relayout()` clears and repopulates `ConnectorLayer.Children` (a
`Canvas` sitting *beneath* the card `ItemsControl` in the same `Grid` cell, so cards always paint over
edges):

```csharp
private static Path BuildConnector(AgentGraphEdge edge)
{
    var figure = new PathFigure { StartPoint = new Point(edge.StartX, edge.StartY), IsClosed = false };
    figure.Segments.Add(new BezierSegment(
        new Point(edge.C1X, edge.C1Y), new Point(edge.C2X, edge.C2Y), new Point(edge.EndX, edge.EndY),
        isStroked: true));

    var geometry = new PathGeometry();
    geometry.Figures.Add(figure);

    return new Path
    {
        Data = geometry,
        StrokeThickness = 1.5,
        StrokeEndLineCap = PenLineCap.Round,
        IsHitTestVisible = false,   // decorative: never steals a click from a card, never a tab stop
    };
}
```

`Stroke` is **not** set in code: it is `{DynamicResource StrokeStrongBrush}` applied via a keyed
`AgentGraphConnectorPathStyle` in `AgentGraphControl.xaml`'s resources, with a `DataTrigger`-free
running-edge variant handled by the caller setting `path.SetResourceReference(Shape.StrokeProperty, ...)`
to `"AccentBrush"` when the child node `IsRunning` — so a live agent's edge is visibly warmer, while the
edge never becomes the *only* signal for that state (7.6). This keeps the palette in `Theme.xaml`, per
`CLAUDE_DESIGN.md` §4 ("consume theme resources by key and essentially never hardcode a color inline").

**Update triggers** — connectors are rebuilt wholesale inside `Relayout()`, which runs on exactly three
events: `Loaded`, `SizeChanged`, and `Nodes.CollectionChanged`. Rebuilding all of them is correct at this
scale (a session has single-digit live agents) and removes any incremental-diff bug class; if the count
ever grows, the fix is pooling `Path` instances inside `Relayout`, not a second update path.

### 7.4 Node visual

**Decision: a `DataTemplate` over an `ItemsControl` with a `Canvas` `ItemsPanel` — not a fully
custom-drawn control.** Only the connector layer is custom geometry.

Justification: every element of the card already exists as templated XAML in panel A's
`HierarchicalDataTemplate` (`MainWindow.xaml:438-485`) — the letter-in-chip `Border` + `TextBlock` bound to
`ModelBadge.ColorHex`/`ModelBadge.Letter` through `{StaticResource HexToBrush}` (`:449-454`), the
`controls:EffortBarsControl Level="{Binding EffortLevel}"` (`:460-462`), and a `TextTrimming="CharacterEllipsis"`
name `TextBlock` on a `StateTextStyle`-derived style (`:479-484`). A custom-drawn card would have to
re-implement text measurement, ellipsis trimming, tooltips and automation peers by hand, and could not
host `EffortBarsControl` (a `UserControl`) at all. `EffortBarsControl`'s own scope is the precedent:
draw only what XAML cannot express, and that is the bezier, not the card.

**Card content** (`176 x 52`, a `Border` with `Background="{StaticResource SurfaceElevatedBrush}"`,
`CornerRadius="{StaticResource RadiusMedium}"`, `BorderThickness="1"`, `Padding="8,6"`):

```
Grid, 2 rows x 4 columns:
  row 0: [state glyph] [model badge chip 18x18] [EffortBarsControl] [name, Star width, ellipsis-trimmed]
  row 1: [3px accent bar spacer] [duration · tokens · context — one TextBlock, spans cols 1-3]
```

| Element | Binding | Source |
|---|---|---|
| state glyph | `{Binding VisualState.Glyph}` | `SessionVisualStateResolver.RunningGlyph`/`IdleGlyph` (`●`/`○`) |
| model badge background | `{Binding ModelBadge.ColorHex, Converter={StaticResource HexToBrush}}` | `ModelBadgeTable.Resolve(Columns.Model)` |
| model badge letter | `{Binding ModelBadge.Letter}` | same |
| effort ring | `Level="{Binding EffortLevel}"` | `EffortBarLevel.Resolve(Columns.Effort)` |
| name | `{Binding DisplayName}` | `Columns.Name`, falling back to `Columns.Type` for an unnamed agent, then `Columns.Id` |
| detail line | `{Binding DetailText}` | `$"{Columns.Duration} · {Columns.Tokens} · {Columns.Context}"` — section 6's `FormatDuration`/`FormatTokenCount` output, unmodified |

`DetailText` is composed in `AgentGraphNodeViewModel`, not in XAML, so the separator/ordering is one
testable string (and so the em-dash "no data" values section 6.7 produces render as `"— · — · "` rather
than a blank strip — the panel never lies about missing data by omitting it).

**Context caveat.** Section 6.4/6.7's `ConsumedTokensIsContextOnly` is surfaced here rather than dropped:
a **session** node's `ToolTip` ends with `" (session tokens are context-window usage: input + cache, no
output tokens)"`. Agent nodes get no such suffix. This is the "render the caveat, not average the two"
requirement from 6.7, and it lives in the tooltip precisely because it must not compete with the number
for space in a 52 px card.

**Visual states**, on the card `Border`, reusing `MainWindow.xaml:34-37`'s four brushes verbatim (moved
into `AgentGraphControl.xaml`'s own `ResourceDictionary` as the same four keys — the codebase already
duplicates these against `SessionVisualStateResolver`'s `*ColorHex` constants and documents why at
`MainWindow.xaml:24-33`; panel E adds a third consumer of the same mapping, not a fifth colour):

| `IsRunning` | `IsFocused` | Border | Name text |
|---|---|---|---|
| true | true | `BorderBrush=RunningFocusedBrush`, `BorderThickness=2`, 3 px left accent bar visible | Bold, `RunningFocusedBrush` |
| true | false | `BorderBrush=RunningBrush`, `BorderThickness=1` | Bold, `RunningBrush` |
| false | true | `BorderBrush=IdleFocusedBrush`, `BorderThickness=2`, 3 px left accent bar visible | Bold, `IdleFocusedBrush` |
| false | false | `BorderBrush=StrokeBrush`, `BorderThickness=1` | Normal, `IdleBrush` |

expressed as `MultiDataTrigger`s in a keyed `AgentGraphCardBorderStyle`, structurally identical to
`StateTextStyle`'s triggers (`MainWindow.xaml:52-83`); the name `TextBlock` uses a
`AgentGraphNodeNameTextStyle` `BasedOn="{StaticResource StateTextStyle}"`, exactly as
`TreeRowNameTextStyle` does (`MainWindow.xaml:94-101`). The 3 px left accent bar for the focused state is
this codebase's existing selection convention (`CLAUDE_DESIGN.md` §4: "often reinforced with a left accent
bar … `ListBoxItem`, `TreeViewItem`, `TabItemContainerStyle` all use a 2-3px `AccentBar`").

**Positioning.** The `ItemsControl.ItemContainerStyle` sets `Canvas.Left="{Binding X}"` /
`Canvas.Top="{Binding Y}"` against `AgentGraphNodeViewModel`'s two `[ObservableProperty]` doubles, which
`Relayout()` writes from `AgentGraphLayout.Compute`'s result. Cards therefore move by property
notification, with no per-card code-behind.

### 7.5 Empty and degenerate states

Panel E must never render an empty rectangle. `AgentGraphViewModel` exposes `StatusText` (an
`[ObservableProperty]`, same name and role as `RootsPanelViewModel.StatusText`, `RootsPanelViewModel.cs:373`)
plus two flags used purely for `Visibility`:

```csharp
[ObservableProperty] private string _statusText = "Waiting for telemetry…";  // verbatim RootsPanelViewModel's initial value
[ObservableProperty] private bool _hasGraph;      // a focused session node was found -> show the canvas
[ObservableProperty] private bool _hasAgents;     // that session has >= 1 child -> hide the "no sub-agents" hint
```

| Situation | `HasGraph` | `HasAgents` | Rendered |
|---|---|---|---|
| No snapshot yet | false | false | centred `SecondaryTextStyle`: `"Waiting for telemetry…"` |
| Snapshot arrived, **no focused session** | false | false | `"No session focused — select a session in the tab strip to see its agent graph."` |
| Focused session **not in this snapshot** (just ended, transcript gone, or never tracked) | false | false | `"Session {id} is no longer in the tree."` where `{id}` is `MonitorTreeBuilder`'s 12-char truncation of the focused id |
| Focused session present, **0 live agents** | true | false | parent card **plus** a muted `SecondaryTextStyle` label `"No sub-agents running"` positioned at `(Padding + CardWidth + ColumnGap, verticalCentre)` — i.e. exactly where the first child would be, so the empty branch reads as "nothing here yet", not as a broken layout |
| Focused session present, **ended/historical** (`State != MonitorNodeState.Live`) | true | per agents | cards render with the idle glyph/weight/colour of 7.4; `StatusText` = `"Session ended — showing last known state."` and is shown as a caption under the header rather than replacing the graph |
| `SnapshotFailed` | unchanged | unchanged | last good graph stays on screen; `StatusText = $"Refresh failed: {message}"` (`RootsPanelViewModel.cs:565`'s exact format) |

The status text and the graph are **not** mutually exclusive in the last two rows — the header row always
shows `"AGENT GRAPH"` (`SectionHeaderTextStyle`, kept from the current stub) with `StatusText` as a
`FontSizeCaption`/`TextMutedBrush` suffix on the same line, so a stale-but-visible graph always says why
it is stale.

### 7.6 Accessibility

The hard rule (`MainWindow.xaml:39-45`, `CLAUDE_DESIGN.md` §4: "Never color-only for state — always pair
color with shape/weight/text", plus "every interactive element needs `AutomationProperties.Name`") applies
unchanged. Per graph node:

| Signal | Non-colour carrier | Colour carrier |
|---|---|---|
| `IsRunning` | the glyph `●` vs `○` (`SessionVisualState.Glyph`) **and** name `FontWeight` Bold vs Normal | `RunningBrush`/`IdleBrush` family |
| `IsFocused` | `BorderThickness` 2 vs 1 **and** the 3 px left accent bar's `Visibility` | `RunningFocusedBrush`/`IdleFocusedBrush` |
| Effort | `EffortBarsControl`'s arc sweep / filled disc (already shape-distinct per its own doc comment) | its per-level ramp |
| Model | the badge **letter** (O/S/H/F/?) | the chip colour |
| Parent vs child | position (left column vs right columns) **and** the automation name below | — |

Strip colour entirely and every state is still distinguishable — the same property `StateTextStyle`'s
comment claims for panel A.

**`AutomationProperties.Name`**, set on the card `Border` via `AutomationProperties.Name="{Binding AutomationDescription}"`,
built in `AgentGraphNodeViewModel` in the shape of `RootsPanelNodeViewModel.BuildAutomationDescription`
(`RootsPanelViewModel.cs:226-239`) extended with the two new metrics and the parent link:

- parent: `"Session: {name}. {VisualState.AutomationName}. Model {model}, effort {effort}, context {context}, running {duration}, {tokens} tokens."`
- child: `"Sub-agent: {name}, child of session {parentName}. {VisualState.AutomationName}. Model {model}, effort {effort}, context {context}, running {duration}, {tokens} tokens."`

`VisualState.AutomationName` is `SessionVisualStateResolver`'s existing `"Running, focused"`/`"Running"`/
`"Idle, focused"`/`"Idle"` (`SessionVisualStateResolver.cs:64-66`) — reused, not re-worded.

**Connectors are explicitly not accessible objects**: a bezier conveys nothing to a screen reader, so each
`Path` is created with `IsHitTestVisible = false`, `Focusable = false`, and
`AutomationProperties.SetAccessibilityView(path, AccessibilityView.Raw)` so it never appears as an
unnamed element in the automation tree. The parent/child relationship it draws is instead carried
textually by the `"child of session {parentName}"` clause above — that is the accessible equivalent of the
curve, and it is why that clause exists.

The container `ItemsControl` gets `AutomationProperties.Name="Agent graph for the focused session"`, and
the two empty-state `TextBlock`s of 7.5 get `AutomationProperties.LiveSetting="Polite"` so a status change
is announced without stealing focus.

### 7.7 File and class layout

**New files (five):**

| Path | Contents |
|---|---|
| `App/ViewModels/AgentGraphLayout.cs` | `AgentGraphLayoutOptions`, `AgentGraphNodeRect`, `AgentGraphEdge`, `AgentGraphLayoutResult` (all `readonly record struct` / `sealed record`), and `public static class AgentGraphLayout` with the single entry point `Compute` |
| `App/ViewModels/AgentGraphNodeViewModel.cs` | `AgentGraphNodeRole` enum (`Parent`, `Child`) + `sealed partial class AgentGraphNodeViewModel : ObservableObject` |
| `App/ViewModels/AgentGraphViewModel.cs` | `sealed partial class AgentGraphViewModel : ObservableObject, IDisposable` |
| `App/Controls/AgentGraphControl.xaml` | the `UserControl` markup: resources, card `DataTemplate`, `Canvas` items panel, connector layer, empty-state text |
| `App/Controls/AgentGraphControl.xaml.cs` | `Relayout()`, `BuildConnector`, the `Nodes` `DependencyProperty` |

**`AgentGraphLayout` is deliberately WPF-free** — no `System.Windows.Point`, no `Geometry`, only `double`s
in plain record structs. This is the explicit, repeated split `CLAUDE_DESIGN.md` §5 describes
(`SessionVisualStateResolver`/`ModelBadgeTable`/`EffortBarLevel` "return plain data … with no
`System.Windows` dependency", with WPF conversion isolated in `App/Converters/`), and it is what makes 7.8's
layout tests possible in a non-STA xUnit process:

```csharp
public readonly record struct AgentGraphLayoutOptions(
    double CardWidth = 176, double CardHeight = 52,
    double ColumnGap = 56, double RowGap = 10, double Padding = 12);

public readonly record struct AgentGraphNodeRect(int Index, double X, double Y, double Width, double Height);

public readonly record struct AgentGraphEdge(
    int ChildIndex,
    double StartX, double StartY, double C1X, double C1Y, double C2X, double C2Y, double EndX, double EndY);

public sealed record AgentGraphLayoutResult(
    AgentGraphNodeRect Parent, AgentGraphNodeRect[] Children, AgentGraphEdge[] Edges,
    double ContentWidth, double ContentHeight, int RowsPerColumn, int ColumnCount);

public static class AgentGraphLayout
{
    public static AgentGraphLayoutResult Compute(int childCount, double availableHeight, AgentGraphLayoutOptions options = default);
}
```

`Compute` never throws: `childCount < 0` clamps to 0, a non-finite or too-small `availableHeight` clamps to
`2*Padding + CardHeight`, and `options == default` substitutes the documented defaults (a `readonly record
struct`'s `default` has all-zero members, so `Compute` normalizes via a private `Normalize(options)` that
replaces any non-positive member with its default — stated here because it is the one non-obvious line in
the file).

**`AgentGraphNodeViewModel`** — a projection of one `MonitorSessionNode` or `MonitorAgentNode`, built by
`AgentGraphViewModel`, never by the control:

```csharp
public AgentGraphNodeViewModel(
    string key, AgentGraphNodeRole role, MonitorNodeState state, MonitorRowColumns columns,
    long? durationMs, long? consumedTokens, bool isFocused, bool consumedTokensIsContextOnly,
    string parentName = "");

public string Key { get; }                       // session id / agent id - same stable ids panel A keys on
public AgentGraphNodeRole Role { get; }
public MonitorNodeState State { get; }
public MonitorRowColumns Columns { get; }
public long? DurationMs { get; }                 // raw, for future edge/bar scaling (section 6.6's stated purpose)
public long? ConsumedTokens { get; }
public bool IsRunning => State == MonitorNodeState.Live;
[ObservableProperty] private bool _isFocused;
[ObservableProperty] private double _x;          // written by AgentGraphControl.Relayout
[ObservableProperty] private double _y;
public SessionVisualState VisualState { get; private set; }
public ModelBadge ModelBadge { get; }            // ModelBadgeTable.Resolve(Columns.Model)
public int EffortLevel { get; }                  // EffortBarLevel.Resolve(Columns.Effort)
public string DisplayName { get; }
public string DetailText { get; }                // "7m 04s · 148.2K · 12.3% of 1M (assumed)"
public string TooltipText { get; }
public string AutomationDescription { get; private set; }
```

`DurationText`/`TokensText` are intentionally **not** re-declared: `Columns.Duration`/`Columns.Tokens` are
already the formatted strings (section 6.6), and `DetailText` composes them. `partial void OnIsFocusedChanged`
re-derives `VisualState` and `AutomationDescription`, exactly as `RootsPanelNodeViewModel.OnIsFocusedChanged`
does (`RootsPanelViewModel.cs:168-172`).

**`AgentGraphViewModel`** public surface: `ObservableCollection<AgentGraphNodeViewModel> Nodes` (parent
first, then children in `MonitorSessionNode.Agents` order — the layout indexes into it), `StatusText`,
`HasGraph`, `HasAgents`, `Rebuild(RootsTreeDto?)`, `Dispose()`.

**`AgentGraphControl`** exposes one `DependencyProperty`, modelled on `EffortBarsControl.LevelProperty`
(`EffortBarsControl.xaml.cs:19-23`) including its `FrameworkPropertyMetadataOptions.AffectsRender` +
static changed-callback shape:

```csharp
public static readonly DependencyProperty NodesProperty = DependencyProperty.Register(
    nameof(Nodes), typeof(IEnumerable), typeof(AgentGraphControl),
    new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnNodesChanged));
```

`OnNodesChanged` re-hooks `INotifyCollectionChanged` (unsubscribing the old value — the one leak this
control can have) and calls `Relayout()`.

**Wiring — `MainWindow.xaml`** (`:626-637`), replacing the stub `StackPanel`:

```xml
<Border x:Name="PanelE" Grid.Row="2" BorderBrush="{StaticResource StrokeSubtleBrush}" BorderThickness="0,1,0,0"
        Background="{StaticResource SurfaceBrush}">
    <DockPanel LastChildFill="True">
        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="16,10,16,0">
            <TextBlock Text="AGENT GRAPH" Style="{StaticResource SectionHeaderTextStyle}" />
            <TextBlock Text="{Binding StatusText}" Margin="10,0,0,0" VerticalAlignment="Center"
                       FontSize="{StaticResource FontSizeCaption}" Foreground="{StaticResource TextMutedBrush}"
                       TextTrimming="CharacterEllipsis" AutomationProperties.LiveSetting="Polite" />
        </StackPanel>
        <controls:AgentGraphControl x:Name="AgentGraph" Nodes="{Binding Nodes}" Margin="4,0,4,4" />
    </DockPanel>
</Border>
```

`xmlns:controls` is already declared (`MainWindow.xaml:5`).

**Wiring — `MainWindow.xaml.cs`**: `_panelEStub` (`:479`) is deleted along with its construction (`:118`),
its `PanelE.DataContext` assignment (`:120`) and its `Dispose()` (`:130`). `_panelBStub` stays — panel B is
still Phase 5. A new constructor overload appends the view model, keeping the existing six-parameter
overload chaining to it with `null` so no existing call site breaks (the same additive-overload pattern
`:34/:44/:62/:78` already uses):

```csharp
public MainWindow(
    RootsPanelViewModel? rootsPanel, PtyRouteRegistry? ptyRouteRegistry, int ptyWebSocketPort,
    TabsViewModel? tabs, PtyRegistry? sessionRegistry, ISessionSelectionService? selection,
    AgentGraphViewModel? agentGraph)
```

with, in the body, `if (agentGraph is not null) { AgentGraph_ViewModel = agentGraph; PanelE.DataContext = agentGraph; }`
— scoped to panel E only, never `Window.DataContext`, per the rule quoted at `:96-98`. `MainWindow` does
**not** dispose it (see below), matching how it does not dispose `rootsPanel` either.

**Wiring — `Program.cs`**: construct it in the composition root next to `rootsPanel` (`Program.cs:159`),
on the same `feed`/`dispatcher`/`selection` triple:

```csharp
var agentGraph = new Accel.App.ViewModels.AgentGraphViewModel(feed, dispatcher, selection);
...
mainWindow = new Accel.App.MainWindow(rootsPanel, server.PtySessions, port, tabs, sessionRegistry, selection, agentGraph);
```

and dispose it in the existing `mainWindow.Closed` handler (`Program.cs:172-176`) immediately before
`rootsPanel.Dispose()` — before `feed.Dispose()`, so the panel unhooks from a feed that still exists.
`rootsPanel.Start()` on `Loaded` is unchanged and starts the single shared feed for both panels; panel E
needs no `Start()` of its own (it catches up from `feed.Latest` in its constructor).

No other file changes. `Metrics/`, `Server/`, `Cli/`, `Orchestration/`, `Settings/`, `Versioning/` are
untouched by panel E.

### 7.8 Testability plan

The split follows `CLAUDE_DESIGN.md` §3 (xUnit for pure logic; smoke tests for real UI/OS behaviour) and,
concretely, what this repo actually does today: `tests/accel.Tests/` contains **no test that instantiates a
WPF control** — `EffortBarsControl` has no test file at all (only `EffortBarLevelTests.cs`, testing the pure
lookup), and `TerminalViewTests.cs` tests only `TerminalView.BuildAttachScript`, a pure `static` method, for
exactly this reason (its class doc: *"the one piece of the terminal-wiring task that is actually unit-testable
outside a real WebView2"*). `tests/accel.Tests/accel.Tests.csproj` sets no `[STAThread]`/STA collection, so
the practice is structural, not accidental.

**Unit-testable — two new xUnit files:**

- **`tests/accel.Tests/AgentGraphLayoutTests.cs`** (`AgentGraphLayout.Compute`, pure arithmetic):
  - `Compute_ZeroChildren_YieldsNoColumnsAndParentOnlyWidth`
  - `Compute_OneChild_AlignsChildVerticallyWithParent` (asserts `Children[0].Y == Parent.Y`)
  - `Compute_ChildrenFitInOneColumn_AreStackedWithRowGap`
  - `Compute_MoreChildrenThanRowsPerColumn_WrapsIntoASecondColumnToTheRight`
  - `Compute_ShortPanel_YieldsOneRowPerColumn` (`availableHeight: 64` → `RowsPerColumn == 1`)
  - `Compute_TallerPanel_YieldsMoreRowsPerColumn` (`160` → 2, `220` → 3 — pins the 7.2 numbers the
    `RowDefinition` was chosen against)
  - `Compute_TrailingPartialColumn_IsVerticallyCentredIndependently`
  - `Compute_ContentWidth_GrowsOnePitchPerColumn`
  - `Compute_NegativeChildCountOrDegenerateHeight_ClampsAndDoesNotThrow`
  - `Compute_DefaultOptions_AreSubstitutedForZeroMembers`
  - edge math: `Compute_Edge_AnchorsAtParentRightEdgeAndChildLeftEdge`,
    `Compute_Edge_ControlPointsShareTheirAnchorY`,
    `Compute_Edge_ControlPointOffsetIsClampedBetween24And96` (`[Theory]` over short/medium/long `dx`),
    `Compute_SameYChildEdge_IsAStraightHorizontalCurve`.
- **`tests/accel.Tests/AgentGraphViewModelTests.cs`** (projection + status, driven exactly like
  `RootsPanelViewModelTests` — `FakeTelemetryFeed` + `RecordingUiThreadDispatcher` from
  `tests/accel.Tests/TelemetryTestDoubles.cs`, a real `SessionSelectionService` with its writer acquired
  in-test, and `RootsTreeDto` fixtures in `RootsTreeRouteTests`/`MonitorTreeBuilderTests` style):
  - `Rebuild_WithFocusedSession_ProjectsParentFirstThenAgentsInOrder`
  - `Rebuild_ParentNode_CarriesModelBadgeEffortContextDurationAndTokens` (asserts `DetailText` is the
    section-6 formatted strings, not re-derived numbers)
  - `Rebuild_AgentNode_CarriesItsOwnDurationAndTokens`
  - `Rebuild_SessionNode_TooltipCarriesTheContextOnlyTokenCaveat` / `Rebuild_AgentNode_TooltipHasNoCaveat`
  - `Rebuild_NoFocusedSession_ClearsNodesAndSetsNoSessionFocusedStatus`
  - `Rebuild_FocusedSessionAbsentFromSnapshot_SetsNoLongerInTheTreeStatus`
  - `Rebuild_FocusedSessionWithZeroAgents_SetsHasGraphTrueAndHasAgentsFalse`
  - `Rebuild_HistoricalFocusedSession_SetsSessionEndedStatusAndStillRenders`
  - `FocusChange_WithNoNewSnapshot_ReprojectsFromTheCachedSnapshot` (the 7.1 table's third row)
  - `FocusedSessionId_MatchesCaseInsensitively` (pins the `IsFocused` reuse)
  - `SnapshotFailed_KeepsTheLastGoodGraphAndSetsRefreshFailedStatus`
  - `Constructed_WithAFeedThatAlreadyHasASnapshot_ProjectsImmediately`
  - `Dispose_UnhooksFeedEventsAndUnsubscribesFromSelection` (asserts via `FakeTelemetryFeed.HasSnapshotSubscribers`,
    the same hook `TelemetryFeedTests` uses)
  - `AutomationDescription_ChildIncludesTheParentSessionName` (the 7.6 substitute for an accessible edge)

**Not unit-testable — smoke-test/manual territory**, because it needs a real STA UI thread, a real
measure/arrange pass and real `ActualHeight`: `Canvas.Left`/`Top` arrangement, `Path` geometry actually
appearing in the visual tree, `SizeChanged`-driven `Relayout`, and the `MultiDataTrigger` styles resolving.
**No new smoke-test file** — extend the existing `App/TabsE2ESmokeTest.cs` (hidden `tabs-e2e-smoke-test`
verb, `Program.cs:74`), which already drives a real `MainWindow` with real selection changes and numbered
checks (`TabsE2ESmokeTest.cs:180-242`), with three checks in its existing `== check N: … ==` /
`[PASS]`/`[FAIL]` format:

- `== check 7: panel E's graph re-targets when the focused tab changes ==` — assert
  `AgentGraphViewModel.Nodes[0].Key` equals the newly selected tab's session id.
- `== check 8: panel E renders one card container per node ==` — walk the visual tree under
  `mainWindow.AgentGraph` and count realized `ContentPresenter`s in the `Canvas` items panel.
- `== check 9: panel E renders one bezier connector per child ==` — assert
  `ConnectorLayer.Children.OfType<Path>().Count() == Nodes.Count - 1` and that each `Data` is a
  `PathGeometry` whose single `PathFigure`'s single segment is a `BezierSegment`.

This is the smallest honest boundary: everything above the `ActualHeight` read is a pure function and is
unit-tested; everything below it is WPF's own layout system and is smoke-tested.

### 7.9 Implementation order

Each step is independently committable, compiles on its own, and leaves the app working.

1. **`App/ViewModels/AgentGraphLayout.cs`** — the pure layout + edge math, no consumers yet. Tests:
   `tests/accel.Tests/AgentGraphLayoutTests.cs` (7.8). Nothing else in the app references it, so this
   commit cannot regress anything.
2. **`App/ViewModels/AgentGraphNodeViewModel.cs`** — the node projection (`DisplayName`, `DetailText`,
   `TooltipText` incl. the context-only caveat, `ModelBadge`/`EffortLevel`/`VisualState`,
   `AutomationDescription`, `X`/`Y`). Still unreferenced. Tests: fold the node-shape assertions into
   `AgentGraphViewModelTests` in step 3 rather than creating a third file (matching 6.8's rule about not
   splitting a test file for members that live on an existing type).
3. **`App/ViewModels/AgentGraphViewModel.cs`** — feed + selection subscription, `Rebuild`, `StatusText`/
   `HasGraph`/`HasAgents`, `Dispose`. Tests: `tests/accel.Tests/AgentGraphViewModelTests.cs`. **The whole
   data path is now provable headlessly**, before a single line of XAML exists.
4. **`App/Controls/AgentGraphControl.xaml` + `.xaml.cs`** — `Nodes` `DependencyProperty`, the card
   `DataTemplate`, the `Canvas` items panel, `ConnectorLayer`, `Relayout()`, `BuildConnector`, empty-state
   text. Not yet placed in any window; verified by building and by a temporary designer/manual check.
5. **Panel E wiring**: `MainWindow.xaml:626-637` replaced per 7.7, the new `MainWindow` constructor
   overload, removal of `_panelEStub`/its construction/its `Dispose`, and `Program.cs` constructing +
   disposing `AgentGraphViewModel`. **Panel E is now live in the running app.**
6. **`MainWindow.xaml:506` row sizing** — `MinHeight="88" MaxHeight="220"` plus the updated comment at
   `:497-502` (the "still a Phase-6 stub" wording is now false). Deliberately its own commit so a bad
   sizing choice is revertable without reverting the panel.
7. **`App/TabsE2ESmokeTest.cs`** — checks 7/8/9 per 7.8, in the existing numbered/PASS-FAIL format.
8. **Doc pass** — add panel E to `CLAUDE_ARCHITECTURE.md` §2.7's `App/` component list (`AgentGraphViewModel`
   as a second `ITelemetryFeed` reader and a second `ISessionSelectionService` reader; `AgentGraphControl`
   next to `EffortBarsControl`), note the new WPF-free `AgentGraphLayout` alongside
   `SessionVisualStateResolver` in `CLAUDE_DESIGN.md` §5's list of deliberately-WPF-free pure logic, and
   mark this section's design as implemented.

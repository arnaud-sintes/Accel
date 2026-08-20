# Accel — Design Rules, Code Style & Conventions

This file documents *how code in this repo is actually written* — naming, patterns, error
handling, comment style, testing conventions, and the WPF/XAML design system — as observed
across `Cli/`, `Server/`, `Orchestration/`, `Settings/`, `Metrics/`, `App/`, and
`tests/accel.Tests/`. It intentionally does not cover architecture/component boundaries
(see `CLAUDE_ARCHITECTURE.md`) or folder layout/build/test commands (see `CLAUDE_ENV.md`).

## 1. C# code style

**File-scoped namespaces, `using` after the namespace.** Every file in `Cli/`, `Server/`,
`Orchestration/`, `Settings/`, `Metrics/`, `App/` starts with `namespace Accel.X;` followed by a
blank line and then `using` directives (not the usual "usings on top" convention) — e.g.
`Cli/ArgParser.cs`, `Orchestration/PtyRegistry.cs`, `Settings/SettingsMerger.cs`,
`App/ViewModels/TabsViewModel.cs` all do this consistently. `Metrics/MetricsPipeline.cs` is one
of the few exceptions that puts a single `using System.Text.Json;` before the namespace — not a
convention, just a one-off.

**Nullable reference types are on and used precisely.** `string?`, `int?`, pattern-matched
`is not null`/`is { } x` idioms are everywhere (e.g. `PtyRegistry.TryGetProcessId`,
`MetricsPipeline`'s `GetString`/`GetLongOrNull` helpers). `ArgumentNullException.ThrowIfNull(...)`
and `ArgumentException.ThrowIfNullOrEmpty(...)` (the newer .NET 8 static guard helpers) are the
standard way to guard public constructor/method parameters — seen in `ArgParser.Parse`,
`PtyRegistry.Register`, `SettingsMerger.Install`, `TabViewModel`'s constructor.

**Records for immutable data, sealed classes for services/engines.** Small immutable DTOs are
`sealed record` (`PtyCloseResult`, `PtyRegistration`, `FoundHookEntry`, `AgentRecord`,
`SessionSnapshot`) — often with XML-doc'd positional parameters. Stateful engines/services are
`sealed class` (`PtyRegistry`, `EventServer`, private nested `Entry`/`ProcessObserver` classes).
`readonly record struct` is used for tiny pure value types with no identity
(`SessionVisualState` in `App/ViewModels/SessionVisualStateResolver.cs`). Static classes with
only static members are used for pure/stateless logic (`ArgParser`, `SettingsMerger`,
`MetricsPipeline`, `SessionVisualStateResolver`) — these are also the classes kept deliberately
WPF-free so they stay unit-testable (see §5).

**Enums for closed outcome sets, not booleans.** Wherever an operation can end in more than
"succeeded/failed", the codebase reaches for an enum with an XML doc on every member explaining
when it applies — `PtyCloseOutcome` (`NotFound`/`Closed`/`ForceKilled`/`ForceKillFailed`/
`ExitUnverified`/`Faulted`), `InstallState`, `StatusLineOwnership`, `InstallOutcome`. This shows
up repeatedly enough to call it a house style: prefer a named outcome enum over `bool success` +
an out-parameter or a thrown exception.

**"Never throw on a hot/tolerant path" is a pervasive idiom.** Any code that runs on an incoming
HTTP request, a hook payload, or a settings.json read is written to swallow exceptions and
degrade gracefully rather than propagate. `EventServer.SafePrint`, every `MetricsPipeline.Handle*`
method (wrapped in `try { ... } catch { /* Best-effort: ... */ }`), `PtyRegistry.CloseOneNeverThrowsAsync`,
`ArgParser.Parse` ("Never throws — an unparseable `--port` value is ignored"). The catch blocks are
never empty without comment — there is always a one-line rationale for the swallow. Conversely,
constructors/registration paths (`PtyRegistry.Register`, `SettingsMerger.Install`) *do* throw
(`ArgumentException`, `ObjectDisposedException`) — the rule is "throw at the edge where a
programmer error should be caught immediately; swallow on the path where an external/untrusted
payload must never take down the process."

**Async conventions.** `ConfigureAwait(false)` is used consistently in library/orchestration code
that might be awaited from the WPF dispatcher (`PtyRegistry`'s internal awaits), explicitly called
out in doc comments ("every internal await is `ConfigureAwait(false)`, so awaiting them from the
WPF dispatcher cannot deadlock"). UI-facing async methods that must resume on the UI thread instead
use `ConfigureAwait(true)` explicitly (`TabsViewModel.CloseTabAsync`, `.StopTabAsync`,
`.AttachSafelyAsync`) rather than omitting the call — the codebase treats "which context this
resumes on" as something to state explicitly, not leave implicit. Static factory-style async
launches use `Task.Run` for CPU/blocking work explicitly moved off the caller's thread
(`PtyRegistry.BeginOrJoinClose`), with a comment explaining why (`PtySession.Dispose` blocks).

**Interlocked/volatile primitives over locks for hot concurrent state.** `PtyRegistry` uses
`ConcurrentDictionary`, `Interlocked.Exchange`, `Volatile.Read`, and `TaskCompletionSource` with
`RunContinuationsAsynchronously` rather than `lock` — and every one of these choices is explained
in a doc comment (see `PtyRegistry`'s "three races this class exists to get right" remarks). This
is the densest concurrency code in the repo; elsewhere locks are simply not needed because state
lives on the UI thread or is single-writer.

**Guard clauses / early return over nested `if`.** Methods consistently validate and `return`
early (`ArgParser.Parse`, `TabsViewModel.SelectTab`, `MetricsPipeline` helpers) rather than wrap
the "happy path" in a big conditional.

**Switch expressions over switch statements** for simple mappings (`ArgParser.ParseVerb`,
`SessionVisualStateResolver.Resolve`, `MetricsPipeline.GetTaskModelId`/`GetTaskEffort`,
`RootsPanelNodeViewModel.DisplayText`).

## 2. Comments and documentation style

**Every public type and public member gets an XML doc comment**, and it is not boilerplate — it
explains *why*, not just *what* (e.g. `PtyCloseOutcome.ExitUnverified`'s doc explains the PID-reuse
safety reasoning inline, not just "exit could not be verified"). Class-level doc comments
routinely run to several paragraphs with `<para>`, numbered `<list type="number">`, and
cross-references via `<see cref="...">` — `PtyRegistry`'s class comment is the extreme example
(covers ownership rules, ordering invariants, three named races, PID-reuse defense, and threading
guarantees, all in doc comments, not a separate design doc).

**Inline `//` comments explain *why*, especially at the point of a subtle decision**, not what the
next line does. Examples: `EventServer.MapRoutes`'s route-by-route phase annotations, `PtyRegistry`'s
"Removal BEFORE disposal" comments, `MainWindow.xaml`'s comments on WPF quirks (e.g. Popup not
inheriting `TextOptions.*`, a markup-compiler miscompilation for deeply nested inline `Click=`
handlers). Trivial/self-evident lines are not commented — comments are reserved for non-obvious
rationale, edge cases, and historical "why not the simpler thing" context.

**Phase/task tags as a traceability convention.** Comments and doc comments are frequently
prefixed with a phase or task id lifted from the project plan — `Phase 3b-ii`, `Phase UI-D`,
`P1-T4`, `P2-T6`, `P3-T1`, `P3-T2`, `P4-T5`, "locked-in decision N". This is pervasive across
`Server/EventServer.cs`, `Orchestration/PtyRegistry.cs`, `App/ViewModels/TabsViewModel.cs`,
`App/MainWindow.xaml`. New code should keep tagging non-trivial decisions this way so a reader can
trace *when and why* a rule was introduced, even without external docs.

**No copyright/license headers, no author tags, no `TODO`/`FIXME` markers observed** in the
sampled files — open questions are instead resolved into an explicit doc-comment statement of the
current, deliberate behavior (or explicitly deferred to a named future phase/task).

## 3. Testing conventions

**Two distinct test mechanisms are used for two distinct purposes — do not blur them:**

- **xUnit unit tests** (`tests/accel.Tests/*.cs`) test pure logic against fakes/fixtures — no real
  process, no real file system beyond temp files, no real network. Naming pattern:
  `MethodOrScenario_Condition_ExpectedResult`, e.g. `NullOrWhitespaceInput_YieldsEmptyArray`,
  `QuotedValueContainingASpace_IsNotReSplit`, `MultipleWhitespaceRuns_CollapseToOneSeparator`. Test
  classes are named `<TypeUnderTest>Tests` (`SettingsMergerTests`, `ExtraArgsParserTests`,
  `PtyRegistryTests`). `[Fact]` is used for concrete scenarios; large realistic JSON fixtures are
  kept as `private const string ... = """ ... """;` raw string literals with a comment describing
  what real-world shape they approximate (`SettingsMergerTests.RealWorldFixture`). Test method
  bodies favor `Assert.Equal(expected, actual)` with plain arrays/values, and a comment sometimes
  demonstrates *why* the naive approach would fail (`QuotedValueContainingASpace_IsNotReSplit`
  builds both the correct and the naive-`Split` result and asserts they differ) — this is a
  distinctive pattern: proving the test is non-vacuous, not just asserting the answer.
- **Smoke tests** (`*SmokeTest.cs` in `Orchestration/` and `App/`, e.g.
  `PtySessionSmokeTest`, `ConPtySmokeTest`, `PtyRegistryStressTest`, `TabsE2ESmokeTest`,
  `TerminalE2ESmokeTest`) are hidden, undocumented CLI dev verbs (not run by `dotnet test`) that
  exercise **real OS/process/UI behavior** unit tests structurally cannot: real child processes,
  real Job Objects, a real WPF window + WebView2 + Kestrel server. They are `static class`es with a
  `public static int Run(TextWriter output, ...)` entry point, run a numbered sequence of "checks"
  (`== check 1/7: ... ==`), print `[PASS]`/`[FAIL]` lines with a human-readable explanation of what
  passing proves, and end with a single summary line (`"pty-session-smoke-test: ALL CHECKS PASSED"`
  or `"N CHECK(S) FAILED"`), returning `0`/`1` as an exit code. Every smoke test's class doc comment
  states explicitly *why it exists as a smoke test rather than a unit test* (real OS lifecycle, real
  UI binding, etc.) and *why it launches `cmd.exe` rather than `claude.exe`* (predictable, no auth,
  no side effects). New smoke tests should follow this exact shape: numbered checks, PASS/FAIL
  lines explaining what each proves, single pass/fail summary line, `cmd.exe` as the stand-in child
  process.

**Test fixtures and doubles live in dedicated files**, not inline duplicated per test file —
`tests/accel.Tests/TelemetryTestDoubles.cs` centralizes fakes shared across multiple test classes.

## 4. UI/XAML design system

The entire visual language is centralized in `App/Theme.xaml`, documented in a large header
comment as an explicit design system (self-described reference feel: "Linear / Raycast / Warp —
true-black base, layered elevation, generous spacing, soft strokes instead of hard 1px gray
chrome, rounded corners everywhere"). Individual windows/controls consume theme resources by key
and essentially never hardcode a color inline (the few hardcoded hex values that do exist, e.g.
`MainWindow.xaml`'s `RunningFocusedBrush` group and `EffortBarsControl`'s per-level colors, are
explicitly cross-referenced in comments to the C# constants they must stay in sync with —
`SessionVisualStateResolver`'s `*ColorHex` constants — so the mapping has one source of truth even
though it's expressed in two places).

**Elevation ladder (background layers, dark theme only — no light theme exists):**
| Layer | Hex | Usage |
|---|---|---|
| L0 base | `#0A0A0A` | window / terminal surface |
| L1 surface | `#121212` | side panels |
| L2 elevated | `#191919` | title bar, tab strip, cards, inputs |
| L3 overlay | `#212121` | popups, menus, tooltips, dialogs |
| hover | `#2A2A2A` | hover overlay |
| pressed | `#333333` | pressed overlay |

**Accents:** primary pastel orange `#F0A868` (hover `#F7BE86`, pressed `#D28F52`, 15%/25% tint);
complement teal-blue `#6EC1D6` (hover `#90D4E4`, pressed `#4E9CB0`, 15%/25% tint). Semantic colors:
danger `#E98F8F`, warning `#E8C07D`, success `#8FCB9B`.

**Typography:** `Segoe UI Variable Text` falling back to `Segoe UI` for body text; `Segoe UI
Variable Display` for the display/title style; `Cascadia Code`/`Cascadia Mono`/`Consolas` for mono.
Type scale: Caption 12, Body 14 (the WPF default is overridden to 14, not 12, "for the modern
web-app density this UI wants"), BodyLarge 15, Subtitle 17, Title 20.

**Spacing/radius scale:** spacing grid 4/8/12/16/24 (`SpacingXs`…`SpacingXl`); radius scale 4
(small controls) / 6 (rows, inputs, buttons) / 8 (cards, dialogs, popups) / 12 (outer window
corner via `WindowChrome`).

**Resource-key naming conventions:**
- Raw `Color` resources end in `...Color` (`AccentColor`, `SurfaceHoverColor`); `SolidColorBrush`
  wrappers end in `...Brush` and reference the color via `{StaticResource ...Color}` — colors and
  brushes are always kept as separate resources, never a brush with an inline hex.
- Legacy/alias brush keys are kept intentionally when the palette was re-skinned, so existing XAML
  call sites don't need touching (`WindowBackgroundBrush`, `PanelBackgroundBrush` are aliases of
  the newer `BackgroundBaseBrush`/`SurfaceBrush`) — documented inline as "kept on purpose."
- Keyed style variants follow `<Role><ControlType>Style` (`PrimaryButtonStyle`,
  `SecondaryButtonStyle`, `SubtleButtonStyle`, `SectionHeaderTextStyle`, `FieldLabelTextStyle`,
  `CardBorderStyle`, `TabItemContainerStyle`, `TabCloseButtonStyle`).

**Every themed control gets a full `ControlTemplate`, never just `Background`/`Foreground`
setters** — this is stated explicitly in `Theme.xaml`'s header and holds for `Button`, `TextBox`,
`ComboBox`, `MenuItem`, `ContextMenu`, `ScrollBar`, `TreeViewItem`, `GridSplitter`, `ToolTip`: the
goal is removing native Windows chrome entirely (scrollbar arrows, combo toggle button, tree
expand triangle), not recoloring it. Implicit (`TargetType`-only, no `x:Key`) styles are the
default for a control type; keyed styles layer variants on top via `BasedOn="{StaticResource
{x:Type X}}"`.

**Never color-only for state — always pair color with shape/weight/text**, called out as a "hard
accessibility requirement" repeatedly: `MainWindow.xaml`'s `StateTextStyle` uses `FontWeight`
(bold/normal) for IsFocused and glyph shape for IsRunning independently of color;
`SessionVisualStateResolver` returns a `Glyph` + `IsBold` + `ColorHex` + `AutomationName` tuple, not
just a color; `EffortBarsControl` renders an actual partial/full ring, not just a color change;
every interactive row also sets `AutomationProperties.Name`/`ToolTip` to a plain-text description
of its state (`RootsPanelNodeViewModel.AutomationDescription`, `TabViewModel.AutomationDescription`).

**AvalonEdit theming** (panel D's `FileEditor`, the file editor): brushes always come from
`Theme.xaml` resources, never inline hex. The properties AvalonEdit exposes as real dependency
properties (`Background`, `Foreground`, `LineNumbersForeground`) are bound in `MainWindow.xaml` via
`{StaticResource ...Brush}`; the ones it only exposes behind read-only `TextArea`/`TextView`
properties — which XAML cannot reach — (`SelectionBrush`, `CurrentLineBackground`,
`CurrentLineBorder`) are set in `MainWindow.xaml.cs`'s constructor from the same resource
dictionary (`FindResource`), sharing the frozen brushes rather than allocating new ones. The
never-color-only rule applies to editor state too: a tab's unsaved-changes state is a literal `●`
glyph plus a bold title (weight + glyph, never a color change alone), with the text form reaching
assistive tech through `TabViewModel.AutomationDescription`/`EditStateSuffix`, and the editor pane's
own `AutomationProperties.Name` flips between "File content (editable)" and "File content
(read-only)".

**Selection vs. hover are always visually distinct fills** (teal tint for selection vs. neutral
elevation overlay for hover), often reinforced with a left accent bar (`ListBoxItem`,
`TreeViewItem`, `TabItemContainerStyle` all use a 2–3px `AccentBar` element that only becomes
visible when selected).

## 5. MVVM / UI architecture conventions

**ViewModels use CommunityToolkit.Mvvm** (`ObservableObject`, `[ObservableProperty]`,
`[RelayCommand]`) — seen in every ViewModel (`TabViewModel`, `TabsViewModel`,
`RootsPanelNodeViewModel`, `RootsPanelViewModel`). Generated partial `On<Prop>Changed` hooks are
used to cascade dependent-property notifications (`TabViewModel.OnHasEndedChanged` raises
`StatusSuffix`/`AutomationDescription` too) rather than manually re-raising `PropertyChanged` in
the setter body.

**Pure logic is deliberately kept WPF-free so it is unit-testable without a UI thread.**
`SessionVisualStateResolver`, `ModelBadgeTable`, `EffortBarLevel`, `ModelEffortTable`, and panel E's
`AgentGraphLayout` (the horizontal, column-major tree layout + bezier control-point math behind
`AgentGraphControl`) return plain data (hex strings, enums, record structs, plain `double`s, `bool`)
with no `System.Windows` dependency; WPF-specific conversion is isolated to tiny one-way
`IValueConverter`s in `App/Converters/` (`HexToBrushConverter`, `BoolToEffortTooltipConverter` — the
latter turns `CreateSessionDialogViewModel.EffortSupported` into the disabled Effort combo's
explanatory tooltip — explicit doc comments state this split is precisely so the resolver logic "stays
unit-testable"). This is a repeated, explicit design rule, not incidental.

**Strict single-writer ownership for shared mutable state**, documented as a "locked-in decision."
E.g. `TabsViewModel` is documented as "the only writer of the focused session id" via
`ISessionSelectionWriter`; every other consumer gets the read-only `ISessionSelectionService`.
Similarly `PtyRegistry` is "the single owner of `PtySession.Dispose`" — no ViewModel or Window
holds a `PtySession` reference at all, only opaque `tabId`s and an `IPtySessionHost` projection.
New code that needs to mutate shared state should follow this pattern: one writer interface handed
to exactly one owner, a read-only interface handed to everyone else.

**ViewModels never own OS resources.** `TabViewModel`'s doc comment states directly: "Holds no
`PtySession` reference at all... so nothing here can dispose a session." Code-behind windows
(`MainWindow.xaml.cs`) wire ViewModels to services/registries in the constructor, but teardown
(`PtyRegistry.CloseAllAsync`/`Dispose`) is explicitly kept out of the ViewModel layer.

**Threading discipline: mutate ObservableObject state only on the UI thread**, with events from
background/thread-pool sources marshalled through an injected `IUiThreadDispatcher` (`_dispatcher.Post(...)`
in `TabsViewModel.OnSessionEnded`/`PollFocusedSessionId`). This dispatcher seam is also the test
seam (tests pass a synchronous fake dispatcher).

**Constructors take dependencies as interfaces with test seams, defaulting to production
implementations only in a default-parameter fallback.** E.g. `TabsViewModel`'s `statusReader`
parameter defaults to `pid => ClaudeSessionStatusFile.TryRead(pid)` when null; `PtyRegistry`'s
`ProcessObserverFactory` defaults to `OpenProcessObserver`. No DI container is used anywhere in
the codebase — wiring is manual, done in `Program.cs`/`MainWindow`'s constructor or a smoke test's
setup code.

**Code-behind is used, but only for what XAML/bindings genuinely cannot express** — event-routed
context-menu clicks (`RenameSession_Click`, `ResumeSession_Click`, etc., wired via
per-`MenuItem` `EventSetter` rather than inline `Click=`, due to a documented markup-compiler bug
for deeply nested inline handlers), and window-level concerns (WM_GETMINMAXINFO hook, WebView2
lifetime). Business/state logic is not duplicated in code-behind — it delegates to a
ViewModel command or service method.

## 6. Other consistent idioms

- **Single-responsibility, small files per concept.** Each concern gets its own file even when
  small: `PtyCloseOutcome`, `PtyCloseResult`, `PtyRegistration`, `PtySessionEndedEventArgs`,
  `PtyRegistryOptions`, `IPtyProcessObserver` are all separate top-level types, but several of them
  are co-located in `PtyRegistry.cs` because they only exist to describe that one class's public
  surface — the file is organized around one cohesive subsystem, not one type per file dogmatically.
- **`sealed` by default** for concrete classes not designed for inheritance (`PtyRegistry`,
  `EventServer`... note `EventServer` itself is `public class` not `sealed`, one of few exceptions —
  most orchestration/settings classes are `sealed`). Nested implementation-detail classes
  (`PtyRegistry.Entry`, `PtyRegistry.ProcessObserver`) are `private sealed class`.
- **Static factory/helper methods over constructors** when the "right" construction needs
  validation or picks between variants: `PtyCloseResult.NotFound(tabId)`, `AccelHookSpec`'s
  builder-style `BuildStatusLine()`/`BuildSubagentStatusLine()`, `ModelBadgeTable.Resolve(...)`.
- **Tolerant JSON parsing via `System.Text.Json.JsonDocument`/`JsonElement` with hand-rolled
  `TryGet*` helpers**, not a strongly-typed DTO deserialized straight from the wire — because
  payload shapes are externally controlled and "tolerant of missing/malformed fields" is a hard
  requirement (`MetricsPipeline`'s `GetString`/`GetLongOrNull`/`GetDoubleOrNull`/`GetDecimalOrNull`
  private helpers, reused across all three `Handle*` entry points). `System.Text.Json.Nodes.JsonNode`
  (the mutable DOM, not `JsonDocument`) is used instead wherever the JSON must be *edited in place*
  (`SettingsMerger`, operating on `settings.json`).
- **Ownership/ordering invariants are stated as a numbered or bulleted list in the owning class's
  doc comment**, not left implicit or only in a design doc — see `SettingsMerger`'s "Invariants"
  bullet list and `PtyRegistry`'s ordering rules. When modifying such a class, update the doc
  comment's invariant list in the same change.

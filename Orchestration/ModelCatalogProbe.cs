namespace Accel.Orchestration;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Accel.Metrics;

/// <summary>How a <see cref="ModelCatalogProbe"/> run ended. Reported as data rather than thrown,
/// matching this codebase's <c>PtyCloseResult</c>/<c>SlashCommandResult</c> convention - a failed
/// discovery is an ordinary outcome (the user may be logged out, or on an untrusted folder), never
/// an error worth interrupting startup for.</summary>
public enum ModelCatalogProbeOutcome
{
    /// <summary>The picker was opened, parsed, and at least one model row recovered.</summary>
    Discovered,

    /// <summary>`claude` is missing, or resolved to a shim - see <see cref="PtySession.CreateClaudeSpec"/>.</summary>
    CliUnavailable,

    /// <summary>The TUI painted, but never reached the chat UI: an auth wall or the folder-trust gate.</summary>
    Blocked,

    /// <summary>The picker never appeared, or appeared in a shape this version cannot parse. The
    /// single most likely outcome after a Claude Code redesign, and the reason a built-in fallback
    /// still exists.</summary>
    NotParseable,

    /// <summary>The run was cancelled (app shutting down).</summary>
    Cancelled,
}

/// <summary>The result of one discovery attempt.</summary>
public sealed record ModelCatalogProbeResult(
    ModelCatalogProbeOutcome Outcome,
    ModelCatalog? Catalog,
    string? Detail,
    TimeSpan Elapsed)
{
    public bool Succeeded => Outcome == ModelCatalogProbeOutcome.Discovered && Catalog is not null;
}

/// <summary>
/// Discovers the real model lineup and each model's effort ladder by driving Claude Code's own
/// interactive <c>/model</c> picker in a headless <see cref="PtySession"/> and parsing what it
/// paints (<see cref="TerminalScreenBuffer"/> → <see cref="ModelPickerScreenParser"/>).
///
/// <para><b>Why this exists at all, and why it looks like this.</b> Claude Code exposes no
/// enumeration of the models it knows: <c>--model</c>'s help lists only examples, there is no
/// <c>claude models</c> subcommand, nothing in <c>~/.claude.json</c> or the statsig caches holds a
/// catalog, and the Anthropic <c>/v1/models</c> endpoint needs an API key (useless for a
/// subscription/OAuth machine), returns API ids rather than <c>--model</c> aliases, and says nothing
/// about effort. Per-model effort is worse: <c>claude --model haiku --effort max</c> succeeds
/// <i>silently</i> after clamping the value internally, and no field in
/// <c>--output-format json</c> reports the clamped result. The picker is the only surface that shows
/// either. Probing candidate model names instead was rejected for two reasons: it can only confirm
/// names Accel already guessed (so it can never learn about a new model, which is the entire point),
/// and each valid probe bills a real API call.</para>
///
/// <para><b>This deliberately screen-scrapes, which the rest of the codebase does not.</b>
/// <see cref="SlashCommandDriver"/> drives the TUI by writing stdin and then polling
/// <c>~/.claude/sessions/&lt;pid&gt;.json</c>, explicitly never by reading terminal output, because
/// the TUI's rendering is not a contract. That reasoning is still correct and still applies here -
/// the difference is only that for the model catalog there is no status file and no other source, so
/// the choice is scraping or hardcoding a list that is measurably already wrong. The consequence is
/// accepted rather than hidden: this class is the one place in Accel allowed to parse rendered
/// terminal output, every failure mode degrades to <see cref="ModelCatalog.BuiltIn"/> instead of
/// surfacing an error, and callers must treat a discovered catalog as a cache to be refreshed, never
/// as a guarantee.</para>
///
/// <para><b>Cost and safety.</b> No prompt is ever submitted, so no API call is billed. Only arrow
/// keys and Escape are ever written: the picker's footer reads "Enter to set as default", so
/// committing would rewrite the user's own <c>model</c> setting, and "s" would change the live
/// session's - a scraper that drives a real picker is one keystroke from mutating what it was only
/// meant to read, so those keys are never sent and <see cref="Quit"/> always leaves via Escape.</para>
/// </summary>
public sealed class ModelCatalogProbe
{
    /// <summary>How long to wait for the TUI's first paint. Generous because a cold `claude` start on
    /// a loaded machine is slow, and this runs off the UI thread where waiting costs nothing.</summary>
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(25);

    /// <summary>How long to keep reading frames after the picker has been asked to open.</summary>
    private static readonly TimeSpan PickerBudget = TimeSpan.FromSeconds(15);

    /// <summary>A frame is considered settled once this long passes with no further output.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(1200);

    /// <summary>Per-keystroke settle time during the slider walk. There is nothing to acknowledge a
    /// keypress, so this is a guess at redraw latency - the weakest link in the whole mechanism, and
    /// why a mis-read ladder has to degrade to the built-in one rather than be trusted.</summary>
    private static readonly TimeSpan KeystrokeSettle = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan KeystrokeBudget = TimeSpan.FromMilliseconds(900);

    /// <summary>Generous geometry: a narrow terminal makes the picker wrap or truncate its rows, and a
    /// truncated row parses as a different row rather than as a failure.</summary>
    private const int Columns = 160;
    private const int Rows = 60;

    private const string KeyUp = "\u001b[A";
    private const string KeyDown = "\u001b[B";
    private const string KeyRight = "\u001b[C";
    private const string KeyEscape = "\u001b";

    /// <summary>Rows the picker paints that are not selectable models in their own right. "Default" is
    /// a pointer to whichever model the account defaults to, so adopting it as a <c>--model</c> value
    /// would pin Accel to today's default forever.</summary>
    private static readonly string[] NonModelRowPrefixes = { "Default" };

    private readonly Func<IReadOnlyList<string>, string?, PtyLaunchSpec> _specBuilder;
    private readonly Func<PtyLaunchSpec, PtySessionOptions, PtySession> _sessionStarter;
    private readonly Action<string>? _trace;

    /// <param name="specBuilder">Test seam: overrides how the launch spec is built, so a test can
    /// point the probe at a scripted stand-in instead of the real `claude`.</param>
    /// <param name="sessionStarter">Test seam: overrides the actual <see cref="PtySession.Start"/>.</param>
    /// <param name="trace">Optional diagnostic sink - what the <c>model-picker-probe</c> dev verb
    /// prints its step-by-step report through.</param>
    public ModelCatalogProbe(
        Func<IReadOnlyList<string>, string?, PtyLaunchSpec>? specBuilder = null,
        Func<PtyLaunchSpec, PtySessionOptions, PtySession>? sessionStarter = null,
        Action<string>? trace = null)
    {
        _specBuilder = specBuilder ?? ((arguments, workingDirectory) =>
            PtySession.CreateClaudeSpec(arguments, workingDirectory));
        _sessionStarter = sessionStarter ?? ((spec, options) => PtySession.Start(spec, options));
        _trace = trace;
    }

    /// <summary>
    /// Runs one discovery attempt against <paramref name="workingDirectory"/>. That directory must be
    /// one Claude Code already trusts - a directory it has never seen opens the "is this a project you
    /// trust?" gate instead of the chat UI, and answering that gate on the user's behalf would persist
    /// a trust decision Accel was never asked to make, so the probe reports
    /// <see cref="ModelCatalogProbeOutcome.Blocked"/> instead. Never throws for a failed discovery.
    /// </summary>
    public async Task<ModelCatalogProbeResult> DiscoverAsync(
        string workingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var stopwatch = Stopwatch.StartNew();
        PtyLaunchSpec spec;
        try
        {
            // No --bare: it skips hooks and CLAUDE.md discovery (attractive here), but it also reads
            // neither OAuth nor the keychain, and the lineup the picker shows depends on the account's
            // entitlements - a --bare run was observed rendering per-Mtok API pricing where the real
            // session renders the enterprise plan. Discovering the wrong account's catalog would be
            // worse than not discovering one.
            spec = _specBuilder(Array.Empty<string>(), workingDirectory);
        }
        catch (PtySessionLaunchException ex)
        {
            return new ModelCatalogProbeResult(
                ModelCatalogProbeOutcome.CliUnavailable, null, ex.Message, stopwatch.Elapsed);
        }

        var options = new PtySessionOptions { Columns = Columns, Rows = Rows };
        PtySession session;
        try
        {
            session = _sessionStarter(spec, options);
        }
        catch (Exception ex)
        {
            return new ModelCatalogProbeResult(
                ModelCatalogProbeOutcome.CliUnavailable, null, ex.Message, stopwatch.Elapsed);
        }

        var raw = new StringBuilder();
        using (session)
        using (var collector = new PtyOutputCollector(session, raw))
        {
            try
            {
                return await RunAsync(session, collector, raw, stopwatch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ModelCatalogProbeResult(
                    ModelCatalogProbeOutcome.Cancelled, null, null, stopwatch.Elapsed);
            }
            finally
            {
                Quit(session);
            }
        }
    }

    private async Task<ModelCatalogProbeResult> RunAsync(
        PtySession session,
        PtyOutputCollector collector,
        StringBuilder raw,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        Trace($"Started Claude Code (pid {session.ProcessId})");

        var painted = await collector
            .WaitForQuietAsync(StartupBudget, QuietPeriod, cancellationToken)
            .ConfigureAwait(false);
        Trace($"Claude Code is ready ({painted} chars painted)");
        if (painted == 0)
        {
            return new ModelCatalogProbeResult(
                ModelCatalogProbeOutcome.NotParseable, null, "the TUI never painted", stopwatch.Elapsed);
        }

        var startupScreen = Render(raw);
        if (FindGate(startupScreen) is { } gate)
        {
            Trace($"Stopped: {gate}");
            return new ModelCatalogProbeResult(ModelCatalogProbeOutcome.Blocked, null, gate, stopwatch.Elapsed);
        }

        Trace("Opening the model picker…");

        // The slash command rather than the meta+p chord: both were observed working, but the command
        // survives a keybinding change and does not depend on Alt arriving as an ESC prefix.
        session.WriteText("/model");
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        session.WriteText("\r");

        await collector.WaitForQuietAsync(PickerBudget, QuietPeriod, cancellationToken).ConfigureAwait(false);

        var screen = ModelPickerScreenParser.Parse(Render(raw));
        if (!screen.IsUsable)
        {
            Trace($"The model picker could not be read (chrome={screen.SawPickerChrome}, rows={screen.Rows.Count})");
            return new ModelCatalogProbeResult(
                ModelCatalogProbeOutcome.NotParseable,
                null,
                $"picker chrome={screen.SawPickerChrome}, rows={screen.Rows.Count}",
                stopwatch.Elapsed);
        }

        Trace($"Model picker open: {screen.Rows.Count} rows (Claude Code {screen.ClaudeCliVersion ?? "version unknown"})");

        var walk = await WalkRowsAsync(session, collector, raw, screen, cancellationToken).ConfigureAwait(false);
        var entries = walk.Entries;
        if (entries.Count == 0)
        {
            return new ModelCatalogProbeResult(
                ModelCatalogProbeOutcome.NotParseable,
                null,
                "every picker row was filtered out as non-selectable",
                stopwatch.Elapsed);
        }

        var catalog = new ModelCatalog(
            entries,
            ModelCatalogSource.Discovered,
            screen.ClaudeCliVersion,
            DateTimeOffset.UtcNow,
            walk.DefaultModelHint);

        return new ModelCatalogProbeResult(
            ModelCatalogProbeOutcome.Discovered, catalog, null, stopwatch.Elapsed);
    }

    /// <summary>
    /// Visits every picker row and reads its effort ladder. The picker shows only the tier the
    /// selected row is currently on, never that row's whole range, so the range has to be driven out
    /// of it: park on the row, then press Right one step at a time and record each distinct tier.
    ///
    /// <para>The slider <b>wraps</b> - stepping past the top tier returns to the bottom rather than
    /// stopping - so there is no floor to walk to first and no "it stopped changing" terminator. One
    /// full cycle, ending the first time a tier repeats, is what yields the ladder.</para>
    /// </summary>
    private async Task<WalkResult> WalkRowsAsync(
        PtySession session,
        PtyOutputCollector collector,
        StringBuilder raw,
        ModelPickerScreen screen,
        CancellationToken cancellationToken)
    {
        var entries = new List<ModelCatalogEntry>();
        var visited = new HashSet<int>();
        string? defaultHint = null;

        // Reach the first row from a known position rather than from wherever the cursor happens to
        // sit (the picker opens with the account's current model selected, not with row 1).
        for (var i = 0; i <= screen.Rows.Count; i++)
        {
            await SendAsync(session, collector, KeyUp, cancellationToken).ConfigureAwait(false);
        }

        // Generous cap: the loop's real terminator is revisiting a row (the cursor wrapped or stopped
        // moving), not a count. Sized so a much longer future lineup still walks completely.
        var maxRows = screen.Rows.Count + 8;

        for (var step = 0; step < maxRows; step++)
        {
            // Re-parsed every iteration, and the row is identified by WHICH ROW THE CURSOR IS ON
            // rather than by an index into the snapshot taken before the walk began. Indexing that
            // snapshot was a real bug: the ladders came out attributed to the wrong models entirely
            // (Opus reported as having no effort control, Haiku as having six tiers) because the
            // snapshot's row order and the cursor's actual position were not the same thing - and a
            // frame captured mid-paint can hold fewer rows than the settled screen does, so the
            // snapshot is not even a reliable count.
            var live = ModelPickerScreenParser.Parse(Render(raw));
            var row = SelectedRow(live);
            if (row is null)
            {
                Trace($"No row is selected at step {step} - stopping");
                break;
            }

            if (!visited.Add(row.Number))
            {
                // Back on a row already read: the cursor wrapped, or Down stopped having an effect.
                break;
            }

            if (IsNonModelRow(row))
            {
                // The "Default (recommended)" row is not selectable as a --model value, but it names
                // the model the account currently defaults to ("Sonnet 5 · Org default") - the only
                // non-arbitrary answer to "what should the dialog open on", so it is kept as a hint
                // rather than discarded with the row.
                if (row.Label.StartsWith("Default", StringComparison.OrdinalIgnoreCase))
                {
                    defaultHint = FirstSegment(row.Description);
                    Trace($"Account default: {defaultHint ?? "(none reported)"}");
                }
                else
                {
                    Trace($"Skipped '{row.Label}' (not a selectable model)");
                }

                await SendAsync(session, collector, KeyDown, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Traced before the walk as well as after: reading one ladder is ~7 keystrokes at a 250ms
            // settle each, so without this the status line a user is watching sits unchanged for
            // seconds at a time and reads as hung.
            Trace($"Reading effort levels for {row.Label}…");

            var tiers = await ReadLadderAsync(session, collector, raw, live.CurrentEffort, cancellationToken)
                .ConfigureAwait(false);

            Trace(tiers.Count == 0
                ? $"row {row.Number} '{row.Label}': no effort control"
                : $"row {row.Number} '{row.Label}': {string.Join(" < ", tiers)}");
            entries.Add(ToEntry(row, tiers));

            await SendAsync(session, collector, KeyDown, cancellationToken).ConfigureAwait(false);
        }

        return new WalkResult(entries, defaultHint);
    }

    /// <summary>
    /// Walks the effort slider for whichever row the cursor is currently on and returns its ladder,
    /// ascending. An empty result means the row has no effort control at all (Haiku's shape) -
    /// deliberately distinct from an unknown ladder, see <see cref="ModelCatalogEntry.EffortTiers"/>.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadLadderAsync(
        PtySession session,
        PtyOutputCollector collector,
        StringBuilder raw,
        string? startingTier,
        CancellationToken cancellationToken)
    {
        if (startingTier is null)
        {
            return Array.Empty<string>();
        }

        var tiers = new List<string> { startingTier };

        // Headroom past the built-in vocabulary, so a ladder longer than the one Accel knows is
        // discovered rather than silently truncated to the expected length - which is exactly what
        // would have happened to "ultracode" had this been sized to EffortBarLevel.Levels.
        var maxSteps = EffortBarLevel.Levels.Count + 5;
        for (var step = 0; step < maxSteps; step++)
        {
            await SendAsync(session, collector, KeyRight, cancellationToken).ConfigureAwait(false);
            var next = ModelPickerScreenParser.Parse(Render(raw)).CurrentEffort;
            if (next is null || tiers.Contains(next))
            {
                break;
            }

            tiers.Add(next);
        }

        return OrderAscending(tiers);
    }

    /// <summary>The row the picker's cursor is on. Falls back to the only row when exactly one
    /// exists, since a single-row picker has nothing to highlight against.</summary>
    private static ModelPickerRow? SelectedRow(ModelPickerScreen screen)
    {
        foreach (var row in screen.Rows)
        {
            if (row.Selected)
            {
                return row;
            }
        }

        return screen.Rows.Count == 1 ? screen.Rows[0] : null;
    }

    /// <summary>What one pass over the picker's rows produced: the selectable models, plus whatever
    /// the non-selectable "Default" row said the account defaults to.</summary>
    private sealed record WalkResult(List<ModelCatalogEntry> Entries, string? DefaultModelHint);

    /// <summary>
    /// A wrapping slider yields its tiers as a cycle, which has no intrinsic first element. Ordered
    /// here by <see cref="EffortBarLevel.Resolve"/> so the result is ascending whatever the walk
    /// started on; tiers this version cannot rank are appended in the order they were seen rather
    /// than dropped - a newly added rung Accel does not know yet is exactly what discovery is for.
    /// </summary>
    private static IReadOnlyList<string> OrderAscending(IReadOnlyList<string> tiers)
    {
        var known = new List<string>();
        var unknown = new List<string>();
        foreach (var tier in tiers)
        {
            (EffortBarLevel.Resolve(tier) > 0 ? known : unknown).Add(tier);
        }

        known.Sort((left, right) => EffortBarLevel.Resolve(left).CompareTo(EffortBarLevel.Resolve(right)));
        known.AddRange(unknown);
        return known;
    }

    /// <summary>
    /// Turns a picker row into a catalog entry. <see cref="ModelCatalogEntry.CliValue"/> is the row's
    /// label with any decoration stripped, because that label is what <c>--model</c> accepts
    /// ("Sonnet", "Opus") - the description column carries the version ("Sonnet 5") and is shown to
    /// the user instead.
    /// </summary>
    private static ModelCatalogEntry ToEntry(ModelPickerRow row, IReadOnlyList<string> tiers)
    {
        var cliValue = ExtractCliValue(row.Label);
        var displayName = FirstSegment(row.Description) ?? row.Label;
        return new ModelCatalogEntry(cliValue, displayName, NullIfEmpty(row.Description), tiers);
    }

    /// <summary>
    /// The bare <c>--model</c> value inside a picker label. Labels carry decoration the CLI does not
    /// accept: a parenthesised variant note ("Opus (1M context)") and trailing marker glyphs
    /// ("Sonnet ✦"). Everything from the first parenthesis on is dropped, then trailing and leading
    /// characters that are not letters or digits are trimmed.
    ///
    /// <para>The trim is by character <i>class</i>, not against a list of known glyphs: an earlier
    /// version enumerated the markers it had seen and promptly shipped <c>--model "Sonnet ✦"</c> the
    /// first time the picker used one that was not on the list. A decorative glyph is by definition
    /// something this code has not seen before, so it cannot be enumerated. Trailing digits survive,
    /// which matters for any future version-suffixed name.</para>
    ///
    /// <para>A variant row collapses onto its base family, which is correct for <c>--model</c>: the
    /// 1M-context variant is selected by appending <c>[1m]</c> to the model name, not by passing the
    /// parenthesised label.</para>
    /// </summary>
    internal static string ExtractCliValue(string label)
    {
        var text = label ?? string.Empty;
        // >= 0, not > 0: a label that is *entirely* a parenthesised note names no model, so cutting at
        // index 0 leaves an empty value and IsNonModelRow discards the row - which is the right outcome.
        // Guarding with > 0 instead would salvage the note's text and offer it as a --model value.
        var parenthesis = text.IndexOf('(');
        if (parenthesis >= 0)
        {
            text = text[..parenthesis];
        }

        var start = 0;
        var end = text.Length - 1;
        while (start <= end && !char.IsLetterOrDigit(text[start]))
        {
            start++;
        }

        while (end >= start && !char.IsLetterOrDigit(text[end]))
        {
            end--;
        }

        return end < start ? string.Empty : text[start..(end + 1)];
    }

    private static bool IsNonModelRow(ModelPickerRow row)
    {
        foreach (var prefix in NonModelRowPrefixes)
        {
            if (row.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return ExtractCliValue(row.Label).Length == 0;
    }

    private static string? FirstSegment(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        // The description is "·"-separated ("Sonnet 5 · Efficient for routine tasks · Org default");
        // only the first segment names the model version.
        var separator = description.IndexOf('·');
        var segment = separator > 0 ? description[..separator] : description;
        return NullIfEmpty(segment.Trim());
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Names the gate blocking the chat UI, or null if there is none. Reported rather than
    /// answered - see <see cref="DiscoverAsync"/>.</summary>
    private static string? FindGate(IReadOnlyList<string> screen)
    {
        foreach (var raw in screen)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Contains("Please run /login", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Select login method", StringComparison.OrdinalIgnoreCase))
            {
                return "Claude Code is not logged in";
            }

            if (line.Contains("Is this a project you created or one you trust", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("I trust this folder", StringComparison.OrdinalIgnoreCase))
            {
                return "Claude Code does not trust this folder yet";
            }
        }

        return null;
    }

    private static IReadOnlyList<string> Render(StringBuilder raw)
    {
        lock (raw)
        {
            return TerminalScreenBuffer.Render(raw.ToString(), Columns, Rows);
        }
    }

    private async Task SendAsync(
        PtySession session, PtyOutputCollector collector, string keys, CancellationToken cancellationToken)
    {
        session.WriteText(keys);
        await collector.WaitForQuietAsync(KeystrokeBudget, KeystrokeSettle, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the picker and asks the CLI to exit. Escape first (cancel the picker without committing
    /// anything), then Ctrl+C as the raw byte 0x03 twice - the TUI treats one as "clear the input
    /// line" and only a second as "quit". <see cref="PtySession.Dispose"/>'s Job Object is the
    /// backstop if it honours neither.
    /// </summary>
    private static void Quit(PtySession session)
    {
        try
        {
            session.WriteText(KeyEscape);
            Thread.Sleep(200);
            session.Write(stackalloc byte[] { 0x03 });
            Thread.Sleep(200);
            session.Write(stackalloc byte[] { 0x03 });
            session.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Already gone, or the input pipe already closed - Dispose still reaps it.
        }
    }

    private void Trace(string message) => _trace?.Invoke(message);
}

/// <summary>
/// Drains <see cref="PtySession.Output"/> into a shared buffer so a caller can ask "has the TUI
/// stopped repainting yet" without racing the pump. Not optional: the session's output channel is a
/// bounded queue with backpressure, not a broadcast, so a consumer that stops reading blocks the
/// child mid-frame and every subsequent render sees a half-painted screen.
/// </summary>
internal sealed class PtyOutputCollector : IDisposable
{
    private readonly StringBuilder _sink;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _task;
    private long _charsSeen;

    public PtyOutputCollector(PtySession session, StringBuilder sink)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _task = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in session.ReadOutputAsync(_cancellation.Token).ConfigureAwait(false))
                {
                    lock (_sink)
                    {
                        _sink.Append(chunk);
                    }

                    Interlocked.Add(ref _charsSeen, chunk.Length);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>
    /// Completes once <paramref name="quiet"/> elapses with no new output, or <paramref name="budget"/>
    /// is exhausted. Returns how many characters arrived during the call. Silence before the first
    /// byte is startup latency, not a settled frame, so it never terminates the wait.
    /// </summary>
    public async Task<long> WaitForQuietAsync(
        TimeSpan budget, TimeSpan quiet, CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.StartNew();
        var baseline = Interlocked.Read(ref _charsSeen);
        var lastSeen = baseline;
        var lastChangeAt = startedAt.Elapsed;

        while (startedAt.Elapsed < budget)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);

            var now = Interlocked.Read(ref _charsSeen);
            if (now != lastSeen)
            {
                lastSeen = now;
                lastChangeAt = startedAt.Elapsed;
                continue;
            }

            if (now != baseline && startedAt.Elapsed - lastChangeAt >= quiet)
            {
                break;
            }
        }

        return Interlocked.Read(ref _charsSeen) - baseline;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _task.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cancellation.Dispose();
    }
}

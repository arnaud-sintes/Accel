namespace Accel.Orchestration;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// What to launch in a <see cref="PtySession"/>, as a real argument <i>array</i> plus an explicit
/// executable path - never a command string, and never a shell.
///
/// <para><b>Why argv and not a command line (plan security requirement, P2-T6).</b> Every argument this
/// class will ever carry is attacker-influenced in some way: session display names typed by the user,
/// working directories derived from folder names, free-text extra CLI args. Concatenating those into a
/// string and letting something else re-split it is how quoting bugs become argument injection. So the
/// caller supplies a <see cref="IReadOnlyList{T}"/> of already-separated arguments and this class does
/// the one-way transformation into the single string <c>CreateProcessW</c> requires
/// (<see cref="BuildCommandLine"/>), using the exact quoting rules <c>CommandLineToArgvW</c> reverses.
/// There is no <c>cmd /c</c>, no <c>ComSpec</c>, and no PATH search: <see cref="ExecutablePath"/> is
/// passed as <c>lpApplicationName</c>, so the image that runs is the one that was resolved, not whatever
/// a later PATH lookup finds.</para>
/// </summary>
public sealed class PtyLaunchSpec
{
    private static readonly string[] ShimExtensions = { ".cmd", ".bat", ".ps1" };

    /// <summary>Full path to the image to run. Used as <c>lpApplicationName</c>, so no PATH search and no
    /// shell interpretation happens. For `claude`, produced by <see cref="ClaudeCliLocator"/> via
    /// <see cref="PtySession.CreateClaudeSpec"/>.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Arguments after argv[0], each already separated. May be empty. Elements are never
    /// re-split, so an argument containing spaces or quotes stays one argument.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>Working directory for the child. Null means "inherit Accel's".</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment variables to add/override (null value removes one). Null or empty means the
    /// child inherits Accel's environment unchanged.</summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentOverrides { get; init; }

    /// <summary>
    /// The <c>lpCommandLine</c> string for <c>CreateProcessW</c>: argv[0] (the executable path) followed
    /// by <see cref="Arguments"/>, each quoted per the <c>CommandLineToArgvW</c> rules so the child's own
    /// argv round-trips back to exactly this list.
    /// </summary>
    public string BuildCommandLine()
    {
        var builder = new StringBuilder();
        AppendQuoted(builder, ExecutablePath);
        foreach (var argument in Arguments)
        {
            builder.Append(' ');
            AppendQuoted(builder, argument ?? string.Empty);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Quotes one argument per the rules <c>CommandLineToArgvW</c> (and therefore the CRT, and therefore
    /// node/.NET/Python argv parsing) reverses: only quote when necessary; inside quotes, a run of
    /// backslashes is doubled when it immediately precedes a <c>"</c> or the closing quote, and a literal
    /// <c>"</c> is emitted as <c>\"</c>. An empty argument must be quoted, otherwise it vanishes.
    /// </summary>
    internal static void AppendQuoted(StringBuilder builder, string argument)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                // Trailing backslashes: doubled, so the closing quote is not escaped by them.
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[i]);
            }
        }

        builder.Append('"');
    }

    /// <summary>
    /// Rejects specs this session type cannot honour, <i>before</i> any OS resource is allocated.
    ///
    /// <para>The load-bearing case is the shim check (locked-in decision 2): if `claude` ever resolves to
    /// a <c>.cmd</c>/<c>.bat</c>/<c>.ps1</c> shim instead of a native <c>claude.exe</c>, the correct
    /// launch is <c>node.exe</c> plus the JS entry point, <b>not</b> ConPTY-attaching the shim. That
    /// alternate path is deliberately not implemented (this machine resolves to a native exe), so the
    /// only acceptable behaviour is to fail loudly and say exactly what is wrong - ConPTY-attaching a
    /// <c>.cmd</c> would either fail with a cryptic ERROR_BAD_EXE_FORMAT or, worse, silently run under a
    /// shell whose argv splitting we do not control.</para>
    /// </summary>
    /// <exception cref="PtySessionLaunchException">The spec cannot be launched.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath))
        {
            throw new PtySessionLaunchException("PtyLaunchSpec.ExecutablePath must be a non-empty path to an executable image.");
        }

        // Trailing dots and spaces are stripped from the last path component by Win32 path
        // normalisation, so 'claude.cmd.' and 'claude.cmd ' open the very same file as 'claude.cmd'
        // (measured: File.Exists is true for all three). Path.GetExtension does NOT strip them - it
        // reports "." and ".cmd " respectively - so comparing its raw result against ".cmd" left the
        // guard below bypassable by a path spelling that the OS resolves straight back to a shim.
        var extension = Path.GetExtension(ExecutablePath.TrimEnd('.', ' '));
        foreach (var shimExtension in ShimExtensions)
        {
            if (string.Equals(extension, shimExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new PtySessionLaunchException(
                    $"Refusing to launch '{ExecutablePath}': a '{shimExtension}' shim cannot be attached to a pseudoconsole " +
                    "directly. Per locked-in decision 2 a shim resolution must be launched as node.exe plus the JS entry " +
                    "point instead; that path is not implemented yet. Re-check `Get-Command claude -All` / `accel doctor` " +
                    "on this machine.");
            }
        }

        foreach (var argument in Arguments)
        {
            if (argument is null)
            {
                throw new PtySessionLaunchException("PtyLaunchSpec.Arguments must not contain nulls.");
            }

            if (argument.Contains('\0', StringComparison.Ordinal))
            {
                // CreateProcessW's command line is NUL-terminated, so an embedded NUL truncates it -
                // silently dropping every following argument.
                throw new PtySessionLaunchException("PtyLaunchSpec.Arguments must not contain NUL characters.");
            }
        }
    }
}

/// <summary>Tunables for one <see cref="PtySession"/>. Defaults are the ones the terminal MVP uses.</summary>
public sealed class PtySessionOptions
{
    /// <summary>Initial pseudoconsole width in cells.</summary>
    public int Columns { get; init; } = 80;

    /// <summary>Initial pseudoconsole height in cells.</summary>
    public int Rows { get; init; } = 25;

    /// <summary>
    /// Bounded capacity of the decoded-output channel, in chunks (a chunk is at most
    /// <see cref="ReadBufferSize"/> bytes' worth of text). See <see cref="PtySession"/>'s backpressure
    /// notes for what happens when it fills.
    /// </summary>
    public int OutputChannelCapacity { get; init; } = 512;

    /// <summary>Size of the byte buffer the pump reads the pty with.</summary>
    public int ReadBufferSize { get; init; } = 4096;

    /// <summary>How long <see cref="PtySession.Dispose"/> waits for the pump thread to finish draining
    /// before giving up on it (it is a background thread, so a wedged pump cannot keep the process
    /// alive).</summary>
    public TimeSpan PumpJoinTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The job object the child is assigned to between spawn and resume. Defaults to
    /// <see cref="AccelJobObject.Shared"/> - the process-wide, statically rooted instance. Overriding it
    /// is for tests/diagnostics that want a job they can close on purpose.
    /// </summary>
    public AccelJobObject? JobObject { get; init; }
}

/// <summary>Why <see cref="PtySession.Exited"/> fired.</summary>
public enum PtySessionExitReason
{
    /// <summary>The child ended on its own (user typed <c>exit</c>, `claude` finished, it crashed) with no
    /// teardown having been requested by Accel.</summary>
    ChildExited,

    /// <summary>The child ended after <see cref="PtySession.Dispose"/> had been called, i.e. Accel tore
    /// the session down (tab closed, app shutting down).</summary>
    TornDown,
}

/// <summary>The child's exit, as observed by <see cref="PtySession"/>.</summary>
public sealed class PtySessionExitedEventArgs : EventArgs
{
    public PtySessionExitedEventArgs(int? exitCode, PtySessionExitReason reason)
    {
        ExitCode = exitCode;
        Reason = reason;
    }

    /// <summary>The child's exit code, or null if it could not be read (e.g. the handle was already gone).</summary>
    public int? ExitCode { get; }

    /// <summary>Whether this was a self-exit or the tail end of a teardown.</summary>
    public PtySessionExitReason Reason { get; }
}

/// <summary>A launch that could not even be attempted, or that failed before the session existed.</summary>
public sealed class PtySessionLaunchException : Exception
{
    public PtySessionLaunchException(string message)
        : base(message)
    {
    }

    public PtySessionLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// P2-T3: one live child process - a `claude`, or in the smoke test a <c>cmd.exe</c> - plus its
/// <see cref="ConPtySession"/> and the byte pumps around it. This is the first layer that is a
/// <i>session</i> rather than raw interop: it owns the launch ordering, the output pump's lifetime, the
/// decoded-text stream, the input side, and exit reaping.
///
/// <para><b>Launch ordering (locked-in decision 7).</b>
/// spawn suspended → assign to the job object → resume. In that order, and it is not cosmetic: the job
/// carries <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, so a child that runs before it is assigned has a
/// window in which it can exit, or spawn grandchildren, entirely outside the kill-on-close net - and
/// those grandchildren would survive Accel being killed. <see cref="ConPtyLaunchSpec.CreateSuspended"/>
/// plus <see cref="ConPtySession.ResumeMainThread"/> close that window to zero instructions.</para>
///
/// <para><b>Job object lifetime (P2-T2b finding 1).</b> The job handle is a <see cref="SafeHandle"/>
/// whose finalizer closes it, and closing it kills everything assigned to it. So the job must be rooted
/// for as long as any session is alive. Two independent guarantees: (a) the default job is
/// <see cref="AccelJobObject.Shared"/>, held in a <c>static readonly</c> field and therefore a GC root
/// for the life of the AppDomain; (b) this session keeps its own strong reference to whichever job it
/// used in <see cref="_jobObject"/> until it is disposed, so even a caller-supplied job cannot be
/// collected under a live session.</para>
///
/// <para><b>Output API.</b> Decoded text arrives on a <see cref="ChannelReader{T}"/>
/// (<see cref="Output"/>), with <see cref="ReadOutputAsync"/> as an <c>await foreach</c> wrapper. A
/// channel rather than an event because it gives real backpressure (see below), a natural completion
/// signal, and no re-entrancy on the pump thread; text rather than raw bytes because every consumer
/// (xterm.js in P2-T5b, the slash-command driver in P4-T1) wants characters, and because the decode must
/// happen exactly once, statefully, in one place. Exactly one consumer is expected.</para>
///
/// <para><b>UTF-8 decoding.</b> A single stateful <see cref="Decoder"/> from
/// <see cref="Encoding.GetDecoder"/> lives for the whole session. Per-chunk
/// <see cref="Encoding.GetString(byte[])"/> would be a correctness bug, not a style choice: a pipe read
/// returns whatever bytes happen to be available, so a 3- or 4-byte character regularly straddles two
/// reads, and per-chunk decoding turns each straddling character into replacement characters (visible in
/// a terminal as mojibake on emoji/box-drawing output). The decoder holds the incomplete tail internally
/// and completes it on the next read; at EOF it is flushed once so a genuinely truncated trailing
/// sequence surfaces as a single U+FFFD instead of being dropped.</para>
///
/// <para><b>Backpressure (bounded, with a teardown escape hatch).</b> The channel is bounded
/// (<see cref="PtySessionOptions.OutputChannelCapacity"/>, default 512 chunks ≈ 2 MB of text) with
/// <see cref="BoundedChannelFullMode.Wait"/>: if the consumer stops reading, the pump blocks, stops
/// draining the pty, and conhost eventually blocks the child - which is exactly what a terminal should
/// do (the alternative, an unbounded buffer, means a runaway child grows Accel's heap without limit;
/// dropping chunks instead would corrupt the VT stream, since the bytes are a stateful protocol, not
/// independent messages). The one situation where blocking is wrong is teardown:
/// <c>ClosePseudoConsole</c> can block until the output pipe is drained, so <see cref="Dispose"/> first
/// cancels the pump's token, which flips the pump into <i>drain-and-discard</i> mode - it keeps reading
/// to EOF but stops publishing - and only then closes the pty. That ordering is what stops a stalled
/// consumer from turning into a hung Dispose.</para>
///
/// <para><b>Threading.</b> The pty pipes are not overlapped (see <see cref="ConPtySession"/>'s docs), so
/// the pump is a dedicated background thread doing blocking reads - not <c>Task.Run</c> plus
/// <c>ReadAsync</c>, which would just park a threadpool thread on the same blocking read while pretending
/// to be async. <see cref="Write(ReadOnlySpan{byte})"/> is serialized by a lock and safe from any thread.
/// <see cref="Dispose"/> is idempotent and must not be called from the pump thread (it joins it); doing
/// so throws rather than deadlocking.</para>
/// </summary>
public sealed class PtySession : IDisposable
{
    private readonly ConPtySession _conPty;
    private readonly AccelJobObject _jobObject; // (b) above: keeps the job rooted while this lives.
    private readonly Channel<string> _outputChannel;
    private readonly PtyOutputPump _pump;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TaskCompletionSource<int?> _exitCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly object _writeGate = new();
    private readonly TimeSpan _pumpJoinTimeout;
    private readonly Process? _childProcess;
    private readonly DateTime? _processStartTimeUtc;

    private int _disposed;
    private int _exitSignalled;
    private volatile bool _teardownRequested;
    // -1 = not observed yet; otherwise the PtySessionExitReason captured at that moment. An int rather
    // than a `PtySessionExitReason?` because Nullable<T> cannot be volatile.
    private volatile int _observedExitReasonRaw = -1;

    private PtySession(
        ConPtySession conPty,
        AccelJobObject jobObject,
        Channel<string> outputChannel,
        PtyOutputPump pump,
        Process? childProcess,
        DateTime? processStartTimeUtc,
        TimeSpan pumpJoinTimeout)
    {
        _conPty = conPty;
        _jobObject = jobObject;
        _outputChannel = outputChannel;
        _pump = pump;
        _childProcess = childProcess;
        _processStartTimeUtc = processStartTimeUtc;
        _pumpJoinTimeout = pumpJoinTimeout;
    }

    /// <summary>Raised once, when the child's exit is observed. May fire on a threadpool thread; marshal
    /// to the UI yourself.</summary>
    public event EventHandler<PtySessionExitedEventArgs>? Exited;

    /// <summary>The child's PID.</summary>
    public int ProcessId => _conPty.ProcessId;

    /// <summary>
    /// The child's actual process start time (UTC) as read while it was still <i>suspended</i>, i.e.
    /// provably the process this session launched - or null if it could not be read. Survives
    /// <see cref="Dispose"/> (it is a captured value, not a live query).
    ///
    /// <para><b>Why this exists (P3-T2).</b> It is the PID-reuse guard for anything that later re-opens the
    /// child by PID: <see cref="ProcessId"/> alone identifies a process only for as long as that process
    /// object still exists, and Windows reuses PIDs freely. <see cref="PtyRegistry"/> pairs the two - it
    /// opens its own <c>Process</c> handle for the child and refuses to trust (and therefore refuses to
    /// <c>Kill</c>) any handle whose start time does not match this value, which is what makes its
    /// force-kill last resort incapable of killing an unrelated process that inherited the PID. Same
    /// PID+start-time pairing <see cref="PtyPidRegistry"/> persists for cross-restart reconciliation.</para>
    /// </summary>
    public DateTime? ProcessStartTimeUtc => _processStartTimeUtc;

    /// <summary>Current pseudoconsole width in cells.</summary>
    public int Columns => _conPty.Columns;

    /// <summary>Current pseudoconsole height in cells.</summary>
    public int Rows => _conPty.Rows;

    /// <summary>The job object this session's child was assigned to.</summary>
    public AccelJobObject JobObject => _jobObject;

    /// <summary>
    /// Decoded terminal output. Completes when the pty reaches EOF (child gone / session disposed);
    /// faults only if the pump hit an unexpected error. Exactly one consumer is expected - a
    /// <see cref="Channel{T}"/> is a queue, not a broadcast.
    /// </summary>
    public ChannelReader<string> Output => _outputChannel.Reader;

    /// <summary>
    /// Awaitable equivalent of <see cref="Exited"/>: completes with the child's exit code (or null if it
    /// could not be read). Never faults. This is what a future <c>PtyRegistry</c> awaits to learn a
    /// session ended; <see cref="ExitReason"/> distinguishes self-exit from teardown.
    /// </summary>
    public Task<int?> ExitTask => _exitCompletion.Task;

    /// <summary>Whether <see cref="Dispose"/> has been requested. Together with <see cref="ExitTask"/>
    /// this is the "ended on its own vs was torn down" signal.</summary>
    public bool TeardownRequested => _teardownRequested;

    /// <summary>
    /// <see cref="PtySessionExitReason.TornDown"/> if teardown had been requested when the exit was
    /// observed, else <see cref="PtySessionExitReason.ChildExited"/>.
    ///
    /// <para>Captured at the moment the exit is observed and frozen thereafter - it deliberately does not
    /// recompute from <see cref="TeardownRequested"/>, because a child that ends on its own is very often
    /// disposed a moment later, and a live recomputation would then retroactively relabel a genuine
    /// self-exit as a teardown. That distinction is the whole point of this property for the future
    /// <c>PtyRegistry</c> (it decides whether to report "session ended" to the user). Before any exit has
    /// been observed it reports the best current estimate.</para>
    ///
    /// <para>Freezing at observation is necessary but not sufficient, because <see cref="Dispose"/> is
    /// itself very often the first observer: that is why Dispose looks for an already-completed exit
    /// <i>before</i> it marks teardown. The one case that remains genuinely ambiguous - and is reported as
    /// <see cref="PtySessionExitReason.TornDown"/> - is a child that exits <i>during</i> the teardown, which
    /// is exactly what a well-behaved child does when its stdin is closed.</para>
    /// </summary>
    public PtySessionExitReason ExitReason =>
        _observedExitReasonRaw >= 0
            ? (PtySessionExitReason)_observedExitReasonRaw
            : (_teardownRequested ? PtySessionExitReason.TornDown : PtySessionExitReason.ChildExited);

    /// <summary>Diagnostics for the smoke test / future doctor verb.</summary>
    public long BytesRead => _pump.BytesRead;

    /// <summary>Chunks published to <see cref="Output"/>.</summary>
    public long ChunksPublished => _pump.ChunksPublished;

    /// <summary>Chunks read but dropped because teardown had begun (drain-and-discard mode).</summary>
    public long ChunksDiscarded => _pump.ChunksDiscarded;

    /// <summary>Whether the pump saw EOF on the pty output pipe.</summary>
    public bool PumpSawEof => _pump.SawEof;

    /// <summary>
    /// Whether the pump thread has terminated. After <see cref="Dispose"/> this must be true: it is the
    /// direct, per-session proof that no pump thread was left running (which a process-wide thread count
    /// cannot give, since threadpool injection moves that number for unrelated reasons).
    /// </summary>
    public bool PumpThreadFinished => _pump.HasFinished;

    /// <summary>
    /// Resolves `claude` fresh (never cached across launches - it self-updates in place) and builds a
    /// launch spec for it.
    /// </summary>
    /// <param name="arguments">Arguments after argv[0], already separated (e.g.
    /// <c>["--session-id", guid, "--name", displayName]</c>).</param>
    /// <param name="workingDirectory">The session's root folder.</param>
    /// <param name="environmentOverrides">Optional environment additions/overrides.</param>
    /// <param name="resolution">Test seam: a pre-computed resolution. Production callers leave this null
    /// so the PATH probe runs now, at launch time.</param>
    /// <exception cref="PtySessionLaunchException">`claude` is missing, or resolved to a shim (see
    /// <see cref="PtyLaunchSpec.Validate"/> for why that is fatal here).</exception>
    public static PtyLaunchSpec CreateClaudeSpec(
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null,
        ClaudeCliResolution? resolution = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var resolved = resolution ?? ClaudeCliLocator.Resolve();
        switch (resolved.Kind)
        {
            case ClaudeCliResolutionKind.Missing:
                throw new PtySessionLaunchException(
                    "Cannot launch `claude`: it was not found on PATH. Install Claude Code, or run `accel doctor` " +
                    "to see what this machine resolves.");

            case ClaudeCliResolutionKind.Shim:
                throw new PtySessionLaunchException(
                    $"Cannot launch `claude`: it resolved to the shim '{resolved.Path}', not a native claude.exe. " +
                    "Per locked-in decision 2 a shim must be launched as node.exe plus the JS entry point rather than " +
                    "ConPTY-attached; that path is not implemented. This is a hard stop rather than a best-effort " +
                    "attempt precisely so the difference cannot be discovered as mysterious terminal misbehaviour.");
        }

        var spec = new PtyLaunchSpec
        {
            ExecutablePath = resolved.Path!,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            EnvironmentOverrides = MergeWithChildSessionMarkersStripped(environmentOverrides),
        };

        // Belt and braces: the resolution said NativeExe, but validate the path itself too.
        spec.Validate();
        return spec;
    }

    /// <summary>
    /// Reported bug: a session created through Accel's "Create session" dialog never appeared in
    /// panel A. Root-caused via a real launch: `claude` printed
    /// "Transcript saving is off — inherited CLAUDE_CODE_CHILD_SESSION marker" and never wrote a
    /// transcript file at all - not delayed, not misattributed, simply never written - so panel A's
    /// disk-scan had nothing to discover, no matter how long it waited.
    ///
    /// <para>Every session Accel launches is meant to be an independent, top-level `claude` session,
    /// never a sub-agent/child of anything - but <see cref="ConPtySession"/> inherits the whole parent
    /// process environment by default (locked-in decision: <see cref="PtyLaunchSpec.EnvironmentOverrides"/>
    /// null means "use the real environment verbatim"). If Accel.exe itself is ever run from a shell
    /// that descends from a `claude` process (a Claude Code integrated terminal, a nested dev
    /// workflow, or - as observed while diagnosing this exact bug - an agent's own tool shell), that
    /// parent's <c>CLAUDE_CODE_CHILD_SESSION</c>/<c>CLAUDE_CODE_SESSION_ID</c>/<c>CLAUDECODE</c>
    /// markers leak straight into the new session's environment and make `claude` believe it is a
    /// nested child session, disabling transcript saving as a result.</para>
    ///
    /// <para>Stripped unconditionally (removed via a null override - see
    /// <see cref="ConPtySession.BuildEnvironmentBlock(IReadOnlyDictionary{string, string?}?)"/>'s "null
    /// removes" contract), regardless of whether a caller passed any <paramref name="callerOverrides"/>
    /// - a caller's own explicit value for one of these three names still wins, since it is deliberate
    /// rather than incidental inheritance. <c>CLAUDE_CODE_ENTRYPOINT</c>/<c>CLAUDE_CODE_EXECPATH</c> are
    /// left untouched: they describe how/where `claude` itself was invoked, not session nesting, and
    /// the observed failure message named only the child-session marker.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, string?> MergeWithChildSessionMarkersStripped(
        IReadOnlyDictionary<string, string?>? callerOverrides)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CODE_CHILD_SESSION"] = null,
            ["CLAUDE_CODE_SESSION_ID"] = null,
            ["CLAUDECODE"] = null,
        };

        if (callerOverrides is not null)
        {
            foreach (var (name, value) in callerOverrides)
            {
                merged[name] = value;
            }
        }

        return merged;
    }

    /// <summary>
    /// Launches the child, assigns it to the job object while it is still suspended, resumes it, and
    /// starts the output pump. Either returns a fully live session or throws having released everything
    /// it allocated.
    /// </summary>
    /// <exception cref="PtySessionLaunchException">The spec is unlaunchable, or the OS refused the
    /// launch/job assignment (inner exception carries the Win32 detail).</exception>
    public static PtySession Start(PtyLaunchSpec spec, PtySessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec.Validate();

        options ??= new PtySessionOptions();
        ConPtySession.ValidateSize(options.Columns, options.Rows);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.OutputChannelCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReadBufferSize, 1);

        // Resolve the job object BEFORE spawning: AccelJobObject.Shared creates the job on first use,
        // and a failure there must not leave a suspended orphan behind.
        var jobObject = options.JobObject ?? AccelJobObject.Shared;

        ConPtySession? conPty = null;
        Process? childProcess = null;
        PtySession? session = null;
        try
        {
            conPty = ConPtySession.Start(new ConPtyLaunchSpec
            {
                ApplicationName = spec.ExecutablePath,
                CommandLine = spec.BuildCommandLine(),
                WorkingDirectory = spec.WorkingDirectory,
                EnvironmentOverrides = spec.EnvironmentOverrides,
                Columns = options.Columns,
                Rows = options.Rows,

                // Step 1 of 3. Without this the two steps below are a race, not an ordering.
                CreateSuspended = true,
            });

            // Step 2 of 3: job assignment, while the child still has not executed an instruction. Uses
            // the SafeProcessHandle overload (P2-T2b finding 2) - never DangerousGetHandle from here.
            jobObject.AssignProcess(conPty.ProcessHandle);

            // Open the observer handle while the child is still suspended: it is provably alive, so its
            // PID cannot have been recycled, which makes Process.GetProcessById safe here and nowhere
            // later. This is the Process.Exited reaping seam - a second, independently owned handle, so
            // exit observation keeps working across ConPtySession.Dispose closing its own handle.
            childProcess = TryOpenChildProcess(conPty.ProcessId);

            // Read the start time here, for the same reason the handle is opened here: the child is
            // suspended, so this is provably *this* child's start time and not a recycled PID's. See
            // ProcessStartTimeUtc for who needs it and why (PtyRegistry's force-kill guard).
            DateTime? processStartTimeUtc = null;
            try
            {
                processStartTimeUtc = childProcess?.StartTime.ToUniversalTime();
            }
            catch
            {
                // Unreadable start time is a degraded-but-usable session, exactly like a missing observer
                // handle: consumers treat null as "cannot verify identity by PID".
            }

            var outputChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(options.OutputChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });

            var pump = new PtyOutputPump(
                conPty.OutputStream,
                outputChannel.Writer,
                options.ReadBufferSize);

            session = new PtySession(
                conPty,
                jobObject,
                outputChannel,
                pump,
                childProcess,
                processStartTimeUtc,
                options.PumpJoinTimeout);

            // Start the pump before resuming, so not one byte of the child's output can be produced
            // before somebody is draining the pipe.
            pump.Start(session._stopCts.Token, $"pty-output-pump-{conPty.ProcessId}");

            if (childProcess is not null)
            {
                childProcess.EnableRaisingEvents = true;
                childProcess.Exited += session.OnChildProcessExited;
            }

            // Step 3 of 3.
            conPty.ResumeMainThread();

            // If the child was so short-lived that it exited before the handler was attached, the event
            // may never fire - check explicitly rather than leaving ExitTask hanging forever.
            session.PollForExit();

            return session;
        }
        catch (Exception ex)
        {
            // Once the session object exists it owns everything (including the pump thread and the
            // CancellationTokenSource), so its own teardown path is the only correct cleanup; falling back
            // to disposing the pieces individually would leave the pump running against a closed pipe.
            if (session is not null)
            {
                try
                {
                    session.Dispose();
                }
                catch
                {
                    // Cleanup on a failure path must not mask the original exception.
                }
            }
            else
            {
                childProcess?.Dispose();
                conPty?.Dispose();
            }

            if (ex is PtySessionLaunchException)
            {
                throw;
            }

            throw new PtySessionLaunchException(
                $"Failed to launch '{spec.ExecutablePath}' in a pseudoconsole: {ex.Message}", ex);
        }
    }

    /// <summary><c>await foreach</c> wrapper over <see cref="Output"/>.</summary>
    public IAsyncEnumerable<string> ReadOutputAsync(CancellationToken cancellationToken = default) =>
        _outputChannel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Writes raw bytes to the child's stdin. This is the primitive, deliberately: terminal input is not
    /// text - Ctrl+C is the single byte <c>0x03</c>, arrow keys are escape sequences, bracketed paste is
    /// framed with <c>ESC [ 200 ~</c>. Serialized against other writes.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    /// <exception cref="IOException">The pipe is gone (child already exited).</exception>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_writeGate)
        {
            // Re-check inside the lock: Dispose may have closed the stream while we waited for it.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _conPty.InputStream.Write(bytes);
            _conPty.InputStream.Flush();
        }
    }

    /// <summary>Convenience over <see cref="Write(ReadOnlySpan{byte})"/>: UTF-8 encodes
    /// <paramref name="text"/>. Adds nothing - no newline, no escaping - so callers stay in control of
    /// the exact byte stream.</summary>
    public void WriteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Resizes the pseudoconsole (P2-T5b's <c>{"resize":[cols,rows]}</c> control frame lands here).</summary>
    public void Resize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _conPty.Resize(columns, rows);
    }

    /// <summary>Blocking wait on the child. Never call from the UI thread; prefer <see cref="ExitTask"/>.</summary>
    public bool WaitForExit(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var exited = _conPty.WaitForExit(timeout);
        if (exited)
        {
            PollForExit();
        }

        return exited;
    }

    /// <summary>The child's exit code, or null while it is still running.</summary>
    public int? TryGetExitCode()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _conPty.TryGetExitCode();
    }

    /// <summary>
    /// Idempotent teardown, in the one order that cannot hang:
    /// <list type="number">
    /// <item>observe an exit that has <i>already</i> happened, before anything is marked as teardown - see
    /// the comment on the call itself for why this is load-bearing rather than an optimisation;</item>
    /// <item>mark teardown requested (so a subsequent exit is reported as
    /// <see cref="PtySessionExitReason.TornDown"/>) and cancel the pump's token - the pump stops
    /// publishing and switches to drain-and-discard, which unblocks it even if the consumer had stalled
    /// on a full channel;</item>
    /// <item>dispose the <see cref="ConPtySession"/> - closes stdin, then <c>ClosePseudoConsole</c>. This
    /// is the call that can block until the output pipe is drained, which is why step 2 comes first;</item>
    /// <item>join the pump thread (bounded), then complete the output channel so any consumer's
    /// <c>await foreach</c> ends;</item>
    /// <item>detach and dispose the exit observer, and complete <see cref="ExitTask"/> if the child's exit
    /// was never observed.</item>
    /// </list>
    /// Never throws. Not callable from the pump thread (it would join itself).
    /// </summary>
    public void Dispose()
    {
        if (_pump.IsCurrentThread)
        {
            throw new InvalidOperationException(
                "PtySession.Dispose must not be called from the output pump thread: Dispose joins that thread.");
        }

        // A reentrant Dispose (a subscriber of Exited - which step 1 below can raise - calling back into
        // us) and a concurrent second Dispose both stop here. The winner owns the whole teardown,
        // *including* setting _teardownRequested: a loser must not set that flag, because the winner may
        // be inside step 1 right now deciding whether the child's exit predates this teardown.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 1. Observe an exit that has already happened, BEFORE declaring teardown. If the child is
        // already gone at the moment Dispose is called then this teardown plainly did not cause its exit,
        // so the reason must freeze as ChildExited. Without this, the frozen-at-observation reason was
        // still wrong for the most ordinary consumer shape there is: a consumer whose `await foreach` over
        // Output ends at EOF (i.e. because the child died) and which then disposes the session. The
        // Process.Exited callback is dispatched on a threadpool thread and loses that race essentially
        // always - measured on this machine, 0 of 20 rounds classified such a self-exit correctly before
        // this call existed (pty-session-smoke-test check 4 part B).
        PollForExit();

        _teardownRequested = true;

        // 2.
        try
        {
            _stopCts.Cancel();
        }
        catch
        {
            // A cancellation callback throwing must not abort the rest of teardown.
        }

        // 3. Serialized against Write so we never close the stream mid-write.
        lock (_writeGate)
        {
            _conPty.Dispose();
        }

        // 4.
        _pump.Join(_pumpJoinTimeout);
        _outputChannel.Writer.TryComplete();

        // 5.
        if (_childProcess is not null)
        {
            try
            {
                _childProcess.Exited -= OnChildProcessExited;
                int? exitCode = _childProcess.HasExited ? _childProcess.ExitCode : null;
                SignalExit(exitCode);
            }
            catch
            {
                SignalExit(null);
            }
            finally
            {
                _childProcess.Dispose();
            }
        }
        else
        {
            SignalExit(null);
        }

        _stopCts.Dispose();
    }

    private static Process? TryOpenChildProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (Exception)
        {
            // Losing the observer is a degraded-but-usable session (ExitTask then completes at Dispose,
            // and TryGetExitCode/WaitForExit still work off the owned handle), so it must not fail the
            // launch. Practically unreachable: the child is suspended and therefore alive.
            return null;
        }
    }

    private void OnChildProcessExited(object? sender, EventArgs e) => PollForExit();

    /// <summary>Observes the exit if it has happened, and signals it at most once.</summary>
    private void PollForExit()
    {
        if (Volatile.Read(ref _exitSignalled) != 0)
        {
            return;
        }

        try
        {
            if (_childProcess is not null)
            {
                if (!_childProcess.HasExited)
                {
                    return;
                }

                SignalExit(_childProcess.ExitCode);
                return;
            }

            var exitCode = _conPty.TryGetExitCode();
            if (exitCode is not null)
            {
                SignalExit(exitCode);
            }
        }
        catch
        {
            // Handle already closed (raced with Dispose): Dispose's own step 4 completes ExitTask.
        }
    }

    private void SignalExit(int? exitCode)
    {
        if (Interlocked.Exchange(ref _exitSignalled, 1) != 0)
        {
            return;
        }

        // Freeze the reason before publishing anything, so a subscriber (or a caller awaiting ExitTask)
        // can never observe a reason that is still drifting with _teardownRequested.
        var reason = _teardownRequested ? PtySessionExitReason.TornDown : PtySessionExitReason.ChildExited;
        _observedExitReasonRaw = (int)reason;
        _exitCompletion.TrySetResult(exitCode);
        try
        {
            Exited?.Invoke(this, new PtySessionExitedEventArgs(exitCode, reason));
        }
        catch
        {
            // A subscriber throwing must not take down the threadpool thread this may run on, nor
            // abort Dispose.
        }
    }
}

/// <summary>
/// The production output pump: a dedicated thread doing blocking reads off the pty output pipe, decoding
/// with one stateful <see cref="Decoder"/>, and publishing into a bounded channel.
///
/// <para>Deliberately decoupled from <see cref="ConPtySession"/> - it takes a plain <see cref="Stream"/>
/// and a <see cref="ChannelWriter{T}"/> - so the two things most likely to be wrong (split multi-byte
/// decoding and backpressure/teardown behaviour) are unit-testable against a fake byte source with no
/// real process involved. <see cref="RunLoop"/> is the whole pump and can be called synchronously from a
/// test.</para>
/// </summary>
internal sealed class PtyOutputPump
{
    private readonly Stream _source;
    private readonly ChannelWriter<string> _writer;
    private readonly int _bufferSize;

    private Thread? _thread;
    private volatile bool _sawEof;
    private volatile Exception? _error;
    private long _bytesRead;
    private long _chunksPublished;
    private long _chunksDiscarded;

    internal PtyOutputPump(Stream source, ChannelWriter<string> writer, int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);

        _source = source;
        _writer = writer;
        _bufferSize = bufferSize;
    }

    /// <summary>Bytes read off the pipe, published or discarded.</summary>
    internal long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary>Text chunks handed to the channel.</summary>
    internal long ChunksPublished => Interlocked.Read(ref _chunksPublished);

    /// <summary>Text chunks dropped because the pump had been cancelled (teardown).</summary>
    internal long ChunksDiscarded => Interlocked.Read(ref _chunksDiscarded);

    /// <summary>True once the pipe returned EOF (or an error that means the same thing). Written on the
    /// pump thread and read from others, hence volatile-backed.</summary>
    internal bool SawEof => _sawEof;

    /// <summary>The exception that ended the loop, if it was not a normal teardown error.</summary>
    internal Exception? Error => _error;

    /// <summary>Whether the pump thread has terminated (or was never started).</summary>
    internal bool HasFinished => _thread is null || !_thread.IsAlive;

    /// <summary>Whether the caller is running on the pump thread (used to refuse a self-joining Dispose).</summary>
    internal bool IsCurrentThread => _thread is not null && ReferenceEquals(Thread.CurrentThread, _thread);

    internal void Start(CancellationToken stopToken, string threadName)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("The pump has already been started.");
        }

        // IsBackground: a wedged pump (blocked in a read on a pipe nobody will ever close) must never be
        // able to keep the process alive after Dispose has given up joining it.
        _thread = new Thread(() => RunLoop(stopToken))
        {
            IsBackground = true,
            Name = threadName,
        };
        _thread.Start();
    }

    internal bool Join(TimeSpan timeout) => _thread is null || _thread.Join(timeout);

    /// <summary>
    /// The pump loop. Reads until EOF; publishes decoded text until <paramref name="stopToken"/> is
    /// cancelled, then keeps reading but discards - because <c>ClosePseudoConsole</c> can block until the
    /// output pipe drains, so the pump must outlive the last consumer rather than stop at cancellation.
    /// Always completes the channel writer exactly once on the way out.
    /// </summary>
    internal void RunLoop(CancellationToken stopToken)
    {
        // ONE decoder for the whole session. This is the requirement that makes split multi-byte
        // sequences work: GetChars(..., flush: false) keeps an incomplete trailing sequence inside the
        // decoder and completes it against the next read's bytes. Encoding.UTF8.GetString per chunk would
        // emit U+FFFD for the split character and again for its continuation bytes.
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[_bufferSize];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(_bufferSize) + 4];
        var discarding = stopToken.IsCancellationRequested;

        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = _source.Read(bytes, 0, bytes.Length);
                }
                catch (Exception ex) when (IsExpectedTeardownFailure(ex))
                {
                    // Dispose closes the read end after ClosePseudoConsole; that races this read by
                    // design, and a broken pipe / disposed handle here is a normal end of stream.
                    _sawEof = true;
                    break;
                }

                if (read <= 0)
                {
                    _sawEof = true;
                    break;
                }

                Interlocked.Add(ref _bytesRead, read);

                var charCount = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (charCount == 0)
                {
                    // The whole read was the leading part of a multi-byte sequence. Nothing to publish
                    // yet - and publishing an empty string would be a lie about the stream.
                    continue;
                }

                if (!discarding && stopToken.IsCancellationRequested)
                {
                    discarding = true;
                }

                if (discarding)
                {
                    Interlocked.Increment(ref _chunksDiscarded);
                    continue;
                }

                if (!TryPublish(new string(chars, 0, charCount), stopToken))
                {
                    discarding = true;
                    Interlocked.Increment(ref _chunksDiscarded);
                }
            }

            // Flush the decoder once at EOF: if the stream ended mid-sequence, this surfaces exactly one
            // U+FFFD rather than silently swallowing the truncated bytes.
            var tailCount = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
            if (tailCount > 0 && !discarding)
            {
                TryPublish(new string(chars, 0, tailCount), stopToken);
            }
        }
        catch (Exception ex)
        {
            _error = ex;
            _sawEof = true;
        }
        finally
        {
            // Single completion point for the channel, so a consumer's `await foreach` always ends -
            // whether the pump stopped at EOF, at cancellation, or on an error.
            _writer.TryComplete(Error);
        }
    }

    private static bool IsExpectedTeardownFailure(Exception ex) =>
        ex is IOException or ObjectDisposedException or OperationCanceledException;

    /// <summary>
    /// Publishes one chunk, blocking while the bounded channel is full (that is the backpressure), and
    /// returning false if it can no longer publish at all - cancellation, or a closed channel.
    /// </summary>
    private bool TryPublish(string text, CancellationToken stopToken)
    {
        if (_writer.TryWrite(text))
        {
            Interlocked.Increment(ref _chunksPublished);
            return true;
        }

        try
        {
            // Blocking on purpose: this is a dedicated thread whose entire job is this pipe, and the
            // block is what propagates backpressure through conhost to the child. Cancellation (only
            // ever raised by Dispose) is what unblocks it.
            _writer.WriteAsync(text, stopToken).AsTask().GetAwaiter().GetResult();
            Interlocked.Increment(ref _chunksPublished);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }
}

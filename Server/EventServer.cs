using System.Text;
using Accel.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Accel.Server;

/// <summary>
/// Minimal-API HTTP server that receives Claude Code hook/statusline events and hands
/// them off to <see cref="EventPrinter"/> for tolerant, human-readable printing.
///
/// Transport and printing only (Phase 3 scope): no metrics parsing, no settings.json logic.
/// Always binds explicitly to 127.0.0.1 — never 0.0.0.0 — per project.md.
/// </summary>
public class EventServer
{
    /// <summary>Default port, matching project.md ("default port 40010").</summary>
    public const int DefaultPort = 40010;

    private WebApplication? _app;

    /// <summary>
    /// Phase 3b-ii: the in-memory session/agent metrics store for this
    /// <see cref="EventServer"/> instance. Constructed once and shared across every route
    /// handler wired via <see cref="BuildApp"/>/<see cref="RunAsync"/> - never re-created
    /// per request. Not persisted to disk; see <see cref="Accel.Metrics.SessionState"/>.
    /// A later phase (3d) will expose this read-only over HTTP.
    /// </summary>
    public SessionState State { get; } = new SessionState();

    /// <summary>
    /// Phase UI-C: the configured root folder list (see <see cref="RootFoldersConfig"/>).
    /// Re-read from disk on every access - <see cref="Accel.App.Services.RootFolderEditor.AddRoot"/>
    /// and <c>RemoveRoot</c> mutate <c>accel-folders.json</c> at runtime (panel A's "+ Add root"
    /// button), so a value cached once at construction would silently never reflect those edits
    /// for the lifetime of the process. A missing/malformed config degrades to an empty array
    /// here, same tolerant contract as the loader itself.
    /// </summary>
    public string[] Roots => RootFoldersConfig.Load();

    /// <summary>
    /// Phase UI-D: the scan/merge/cache engine backing <c>GET /roots/tree</c>. One instance per
    /// <see cref="EventServer"/>, so its per-tick caches (see <see cref="RootsTreeBuilder"/>'s
    /// class summary) actually persist across refresh ticks instead of being rebuilt from
    /// scratch on every request.
    /// </summary>
    public RootsTreeBuilder RootsTree { get; } = new RootsTreeBuilder();

    /// <summary>
    /// Phase UI-D test hook: overrides the <c>%USERPROFILE%\.claude\projects</c> base
    /// directory that <see cref="RootsTree"/> scans, so tests can point at a fixture tree
    /// instead of the real filesystem location. Null (the default) means "use the real
    /// location" - see <see cref="RootsTreeBuilder.Build"/>.
    /// </summary>
    public string? ProjectsDirOverride { get; set; }

    /// <summary>
    /// P2-T4: the <c>tabId -&gt; IPtyEndpoint</c> registry backing <c>/pty/{tabId}</c>. One
    /// instance per <see cref="EventServer"/>, same lifetime as <see cref="State"/>/<see cref="RootsTree"/>.
    /// Registration of real sessions is deliberately not wired here - that lands with P2-T6 (session
    /// creation) and P3-T2 (<c>PtyRegistry</c>); see <see cref="PtyRouteRegistry"/>'s class doc.
    /// </summary>
    public PtyRouteRegistry PtySessions { get; } = new PtyRouteRegistry();

    /// <summary>
    /// Builds (but does not start) a <see cref="WebApplication"/> bound to
    /// http://127.0.0.1:{port}, with the five/six event routes mapped.
    /// Exposed as a static method (rather than only an instance method) so tests can
    /// build an isolated instance on an ephemeral port (pass 0) without starting the
    /// "real" listening loop via <see cref="RunAsync"/>.
    ///
    /// <paramref name="dumpRawDir"/> is Phase 3b-i's optional payload-capture mode
    /// (`accel run --dump-raw &lt;dir&gt;`): when non-null, every received event's raw
    /// body is additionally written to a file under that directory - see
    /// <see cref="RawPayloadCapture"/>. Defaults to null (capture disabled), matching
    /// existing Phase 3 behavior exactly.
    ///
    /// <paramref name="state"/> is the Phase 3b-ii metrics store to write into; when null
    /// (the default - preserves the existing static-call signature used by earlier tests)
    /// a fresh, throwaway <see cref="SessionState"/> is created. Prefer the instance method
    /// <see cref="BuildApp"/> when the caller needs to keep reading that store afterwards.
    ///
    /// <paramref name="roots"/> is Phase UI-C's root folder list backing <c>GET /roots</c>;
    /// when null (the default) it is loaded once via <see cref="RootFoldersConfig.Load()"/>.
    /// Tests that want a specific fixture pass an explicit array instead of touching real
    /// filesystem locations.
    ///
    /// <paramref name="rootsTree"/>/<paramref name="projectsDirOverride"/> back Phase UI-D's
    /// <c>GET /roots/tree</c>: <paramref name="rootsTree"/> defaults to a fresh
    /// <see cref="RootsTreeBuilder"/> when null, and <paramref name="projectsDirOverride"/> lets
    /// tests point the scan at a fixture directory instead of the real
    /// <c>%USERPROFILE%\.claude\projects</c>.
    /// </summary>
    public static WebApplication Build(
        int port,
        string? dumpRawDir = null,
        SessionState? state = null,
        string[]? roots = null,
        RootsTreeBuilder? rootsTree = null,
        string? projectsDirOverride = null,
        PtyRouteRegistry? ptySessions = null,
        bool verbose = false)
    {
        var builder = WebApplication.CreateBuilder();

        // Keep terminal output limited to EventPrinter's own lines - the ASP.NET Core
        // request-processing pipeline has nothing interesting to say for this tool's
        // purposes, and default console logging would interleave with our own prints.
        builder.Logging.ClearProviders();

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var app = builder.Build();

        RawPayloadCapture? capture = dumpRawDir is null ? null : new RawPayloadCapture(dumpRawDir);
        SessionState effectiveState = state ?? new SessionState();
        string[] effectiveRoots = roots ?? RootFoldersConfig.Load();
        RootsTreeBuilder effectiveRootsTree = rootsTree ?? new RootsTreeBuilder();
        PtyRouteRegistry effectivePtySessions = ptySessions ?? new PtyRouteRegistry();

        MapRoutes(app, capture, effectiveState, effectiveRoots, effectiveRootsTree, projectsDirOverride, effectivePtySessions, verbose);

        return app;
    }

    /// <summary>
    /// Instance form of <see cref="Build"/> that wires this instance's <see cref="State"/>,
    /// <see cref="Roots"/>, <see cref="RootsTree"/>, and <see cref="PtySessions"/> into the routes,
    /// so callers (Program.cs's `run` verb, and metrics tests) can read the resulting session/agent
    /// records back out afterwards. <paramref name="verbose"/> is `--verbose` (default off): a
    /// regular launch stays silent, only printing the per-event lifecycle lines when opted in.
    /// </summary>
    public WebApplication BuildApp(int port, string? dumpRawDir = null, bool verbose = false) =>
        Build(port, dumpRawDir, State, Roots, RootsTree, ProjectsDirOverride, PtySessions, verbose);

    private static void MapRoutes(
        WebApplication app,
        RawPayloadCapture? capture,
        SessionState state,
        string[] roots,
        RootsTreeBuilder rootsTree,
        string? projectsDirOverride,
        PtyRouteRegistry ptySessions,
        bool verbose = false)
    {
        app.MapPost("/events/session-start", ctx => HandleEventAsync(ctx, "SessionStart", capture, state, verbose));
        app.MapPost("/events/session-end", ctx => HandleEventAsync(ctx, "SessionEnd", capture, state, verbose));
        app.MapPost("/events/subagent-start", ctx => HandleEventAsync(ctx, "SubagentStart", capture, state, verbose));
        app.MapPost("/events/subagent-stop", ctx => HandleEventAsync(ctx, "SubagentStop", capture, state, verbose));
        app.MapPost("/events/post-tool-use", ctx => HandleEventAsync(ctx, "PostToolUse", capture, state, verbose));
        app.MapPost("/events/stop", ctx => HandleEventAsync(ctx, "Stop", capture, state, verbose));
        app.MapPost("/events/status-line", ctx => HandleStatusLineAsync(ctx, capture, state));
        app.MapPost("/events/subagent-status-line", ctx => HandleSubagentStatusLineAsync(ctx, capture, state));

        // Phase 3d: read-only aggregation routes over the same shared SessionState.
        StateQueryRoutes.Map(app, state);

        // Phase UI-C: read-only root folder list, loaded once at startup (see Roots/Build).
        RootsRoutes.Map(app, roots);

        // Phase UI-D: disk-enumerated + live-merged session/agent tree, scanned/cached fresh
        // per request by rootsTree (see RootsTreeBuilder's per-instance caches).
        RootsTreeRoute.Map(app, roots, state, rootsTree, projectsDirOverride);

        // P2-T4: loopback WebSocket PTY transport. See PtyRoutes' class doc for the security
        // posture (Origin check + unguessable tabId) and the binary/text framing convention.
        PtyRoutes.Map(app, ptySessions);
    }

    // Every route: read raw body, best-effort parse, hand off to EventPrinter, always 204.
    // Hook processes (and this project's own curl/statusline callers) never look at the
    // response body, and per project.md, hook processes must never see anything that
    // could be misparsed as a hook control object - so we never throw, never write a body.
    private static async Task HandleEventAsync(HttpContext ctx, string eventName, RawPayloadCapture? capture, SessionState state, bool verbose = false)
    {
        string body = await ReadBodyAsync(ctx.Request);
        if (verbose)
        {
            SafePrint(() => EventPrinter.PrintEvent(eventName, body));
        }

        capture?.TryWrite(eventName, body);

        // Phase 3b-ii: SubagentStop is the only event that additionally triggers transcript
        // tailing/metrics recording. Runs after printing so existing output is unchanged,
        // and is itself fully best-effort (see MetricsPipeline) so it can never affect the
        // 204 response below.
        if (eventName == "SubagentStop")
        {
            SafePrint(() => MetricsPipeline.HandleSubagentStop(body, state));
        }
        else if (eventName == "SessionEnd")
        {
            // Phase 3d eviction: SessionEnd -> session marked ended in SessionState, same
            // best-effort contract as the SubagentStop handling above.
            SafePrint(() => MarkSessionEndedFromPayload(body, state));
        }
        else if (eventName == "PostToolUse")
        {
            // MCP/Skill hit-count tracking: observation-only, same best-effort contract as
            // the SubagentStop handling above.
            SafePrint(() => MetricsPipeline.HandlePostToolUse(body, state));
        }
        else if (eventName == "Stop")
        {
            // "Waiting for feedback" tracking (window-flash/row-highlight): observation-only,
            // same best-effort contract as the SubagentStop handling above.
            SafePrint(() => MarkSessionWaitingFromPayload(body, state));
        }

        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    // Extracts session_id from a SessionEnd payload and marks it ended in SessionState.
    // Kept here (rather than in MetricsPipeline) since it's a one-line lookup, not a metrics
    // parse - MetricsPipeline owns the richer status-line/transcript parsing paths.
    private static void MarkSessionEndedFromPayload(string rawBody, SessionState state)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("session_id", out var sessionIdProp)
            && sessionIdProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            string? sessionId = sessionIdProp.GetString();
            if (!string.IsNullOrEmpty(sessionId))
            {
                state.MarkSessionEnded(sessionId);
            }
        }
    }

    // Extracts session_id from a Stop payload and marks it "waiting for feedback" in
    // SessionState. Same tolerant contract as MarkSessionEndedFromPayload above.
    private static void MarkSessionWaitingFromPayload(string rawBody, SessionState state)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("session_id", out var sessionIdProp)
            && sessionIdProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            string? sessionId = sessionIdProp.GetString();
            if (!string.IsNullOrEmpty(sessionId))
            {
                state.MarkSessionWaiting(sessionId);
            }
        }
    }

    private static async Task HandleStatusLineAsync(HttpContext ctx, RawPayloadCapture? capture, SessionState state)
    {
        string body = await ReadBodyAsync(ctx.Request);
        capture?.TryWrite("StatusLine", body);
        SafePrint(() => MetricsPipeline.HandleStatusLine(body, state));
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    // Phase 3c: the subagentStatusLine payload has a richer shape (a `tasks` array, each
    // entry optionally carrying model/effort/contextWindowSize/tokenCount/tokenSamples/cwd)
    // than the generic hook events, so it gets its own printer entry point rather than the
    // generic PrintEvent used by HandleEventAsync. Same tolerant-always-204 contract.
    private static async Task HandleSubagentStatusLineAsync(HttpContext ctx, RawPayloadCapture? capture, SessionState state)
    {
        string body = await ReadBodyAsync(ctx.Request);
        capture?.TryWrite("SubagentStatusLine", body);

        // Phase 3d: upsert Live agent records from `tasks[]` and reconcile staleness -
        // see MetricsPipeline.HandleSubagentStatusLine for the full contract.
        SafePrint(() => MetricsPipeline.HandleSubagentStatusLine(body, state));

        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static void SafePrint(Action print)
    {
        // Defense in depth: EventPrinter is already fully defensive internally, but a
        // printing failure must never be allowed to turn into a 500 for a hook caller.
        try
        {
            print();
        }
        catch
        {
            // Intentionally swallowed - see rationale above.
        }
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Starts the server and awaits it (i.e. runs until cancelled/stopped). Program.cs's
    /// entry point calls this. A later CLI phase will wire this to a `run` verb.
    /// </summary>
    public async Task RunAsync(int port = DefaultPort, string? dumpRawDir = null, CancellationToken cancellationToken = default)
    {
        _app = BuildApp(port, dumpRawDir);
        await _app.RunAsync(cancellationToken);
    }

    /// <summary>Stops a server previously started via <see cref="RunAsync"/>, if any.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken);
        }
    }
}

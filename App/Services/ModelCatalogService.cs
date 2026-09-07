namespace Accel.App.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Accel.Metrics;
using Accel.Orchestration;
using Accel.Settings;

/// <summary>
/// Owns the model/effort catalog for the running app: hands out one immediately at startup, and
/// refreshes it in the background when the cached one no longer matches the installed `claude`.
///
/// <para><b><see cref="Current"/> is always usable immediately.</b> It is populated synchronously
/// from the on-disk cache (or <see cref="ModelCatalog.BuiltIn"/> when there is none) by the
/// constructor, which spawns nothing - so the UI has a catalog before any window opens, whether or
/// not a refresh ever happens.</para>
///
/// <para><b>Two refresh modes; production uses the blocking one.</b>
/// <c>ModelCatalogUpdateDialog.RunIfNeeded</c> awaits <see cref="RefreshAsync"/> behind a modal
/// "please wait" during the main window's <c>Loaded</c>. That is deliberate: the catalog feeds the
/// Create-session dialog's pickers, and a background refresh meant a user who created a session in
/// the first ~15 seconds silently got the built-in fallback list - the exact failure the catalog
/// exists to prevent. Because a refresh only happens when the cached catalog no longer matches the
/// installed <c>claude.exe</c>, the cost is one wait per Claude Code upgrade rather than per start.
/// <see cref="BeginRefreshIfStale"/> is the non-blocking alternative - kept, tested, and currently
/// unused by the app - for anyone who would rather trade freshness for an uninterrupted start.</para>
///
/// <para><b>Refresh is keyed on the CLI binary, not on a clock.</b> See
/// <see cref="ModelCatalogCache"/>: an unchanged claude.exe means an unchanged lineup, so the common
/// case does no work at all and spawns nothing.</para>
///
/// <para>Nothing here throws for a failed discovery. Every failure path leaves
/// <see cref="Current"/> at whatever it already was, records the reason in
/// <see cref="LastRefreshDetail"/> for diagnostics, and lets the app carry on with the built-in
/// list - which is the whole reason that list still exists.</para>
/// </summary>
public sealed class ModelCatalogService : IDisposable
{
    private readonly Func<string, CancellationToken, Task<ModelCatalogProbeResult>> _discover;
    private readonly Func<string?> _cliPathResolver;
    private readonly string? _cachePath;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();

    private ModelCatalog _current;
    private Task? _refresh;

    /// <param name="discover">Test seam: replaces the real <see cref="ModelCatalogProbe"/> run.</param>
    /// <param name="cliPathResolver">Test seam: replaces PATH resolution of `claude`.</param>
    /// <param name="cachePath">Test seam: an alternate cache file.</param>
    /// <param name="settingsPath">Test seam: an alternate <c>settings.json</c>.</param>
    public ModelCatalogService(
        Func<string, CancellationToken, Task<ModelCatalogProbeResult>>? discover = null,
        Func<string?>? cliPathResolver = null,
        string? cachePath = null,
        string? settingsPath = null)
    {
        _discover = discover ?? ((workingDirectory, token) =>
            new ModelCatalogProbe(trace: message => Progress?.Report(message))
                .DiscoverAsync(workingDirectory, token));
        _cliPathResolver = cliPathResolver ?? (() => ClaudeCliLocator.Resolve().Path);
        _cachePath = cachePath;

        UserDefaults = UserModelDefaults.Read(settingsPath);

        var identity = ModelCatalogCache.DescribeCliBinary(_cliPathResolver());
        _current = ModelCatalogCache.TryLoad(_cachePath, identity) ?? ModelCatalog.BuiltIn;
    }

    /// <summary>The catalog to use right now. Never null; starts out as the cache or the built-in
    /// list, and is replaced wholesale (never mutated) by a successful refresh.</summary>
    public ModelCatalog Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>The user's configured model/effort defaults - see <see cref="UserModelDefaults"/>.</summary>
    public UserModelDefaults UserDefaults { get; }

    /// <summary>
    /// Optional sink for the probe's step-by-step trace, so a blocking UI can show what discovery is
    /// doing instead of an opaque spinner (<c>ModelCatalogUpdateDialog</c> sets this for the duration
    /// of one run). A settable property rather than a <see cref="RefreshAsync"/> parameter because
    /// the injected <c>discover</c> test seam does not - and should not - know about progress
    /// reporting; only the real probe path reads this. Reports arrive on a background thread, so a
    /// <see cref="Progress{T}"/> constructed on the UI thread is the expected implementation.
    /// </summary>
    public IProgress<string>? Progress { get; set; }

    /// <summary>Why the last refresh ended the way it did, for <c>accel doctor</c>-style diagnostics
    /// and the picker's tooltip. Null before any refresh has run.</summary>
    public string? LastRefreshDetail { get; private set; }

    /// <summary>The outcome of the last refresh, or null if none has completed.</summary>
    public ModelCatalogProbeOutcome? LastRefreshOutcome { get; private set; }

    /// <summary>Raised on a background thread when <see cref="Current"/> has been replaced. WPF
    /// consumers must marshal to the UI thread themselves (the same contract
    /// <c>ITelemetryFeed</c>'s events already have) - this class has no WPF dependency, so it cannot
    /// dispatch on their behalf.</summary>
    public event EventHandler<ModelCatalog>? CatalogChanged;

    /// <summary>
    /// True when the cached catalog does not match the installed `claude` (or there is none), i.e. a
    /// refresh would do something. Exposed so a caller can decide whether to bother.
    /// </summary>
    public bool NeedsRefresh()
    {
        var identity = ModelCatalogCache.DescribeCliBinary(_cliPathResolver());
        return ModelCatalogCache.TryLoad(_cachePath, identity) is null;
    }

    /// <summary>
    /// The non-blocking refresh mode: starts one background refresh if one is warranted and none is
    /// already running, then returns immediately. <b>Not used by the app</b> - production blocks
    /// behind <c>ModelCatalogUpdateDialog</c> instead, so that a session created moments after
    /// startup gets the real catalog rather than the fallback (see this class's remarks). Kept as a
    /// supported alternative for the opposite trade-off.
    ///
    /// <para><paramref name="workingDirectory"/> must be a directory Claude Code already
    /// trusts - discovery reports <see cref="ModelCatalogProbeOutcome.Blocked"/> rather than answering
    /// the folder-trust gate on the user's behalf, so passing a fresh temp directory would always
    /// fail. Callers should pass a configured root (see
    /// <see cref="ResolveProbeDirectory(System.Collections.Generic.IEnumerable{string}?)"/>).</para>
    /// </summary>
    public void BeginRefreshIfStale(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !NeedsRefresh())
        {
            return;
        }

        lock (_gate)
        {
            if (_refresh is { IsCompleted: false })
            {
                return;
            }

            _refresh = Task.Run(() => RefreshAsync(workingDirectory, _shutdown.Token));
        }
    }

    /// <summary>
    /// Runs one discovery attempt and, on success, publishes and caches the result. Awaitable for the
    /// dev verb and for tests; production startup uses <see cref="BeginRefreshIfStale"/>.
    /// </summary>
    public async Task<ModelCatalogProbeResult> RefreshAsync(
        string workingDirectory, CancellationToken cancellationToken = default)
    {
        ModelCatalogProbeResult result;
        try
        {
            result = await _discover(workingDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ModelCatalogProbeResult(ModelCatalogProbeOutcome.Cancelled, null, null, TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            // A probe that throws is a bug in the probe, not a reason to take the app down - the
            // built-in catalog is still perfectly serviceable.
            LastRefreshOutcome = ModelCatalogProbeOutcome.NotParseable;
            LastRefreshDetail = ex.Message;
            return new ModelCatalogProbeResult(ModelCatalogProbeOutcome.NotParseable, null, ex.Message, TimeSpan.Zero);
        }

        LastRefreshOutcome = result.Outcome;
        LastRefreshDetail = result.Detail;

        if (!result.Succeeded || result.Catalog is null)
        {
            return result;
        }

        ModelCatalogCache.TrySave(
            result.Catalog, ModelCatalogCache.DescribeCliBinary(_cliPathResolver()), _cachePath);

        lock (_gate)
        {
            _current = result.Catalog;
        }

        CatalogChanged?.Invoke(this, result.Catalog);
        return result;
    }

    /// <summary>
    /// Picks a directory to run discovery in: the first of <paramref name="candidateRoots"/> that
    /// exists. A configured root is used rather than a temp directory precisely because Claude Code
    /// gates unknown directories behind its trust prompt - the user has already trusted the folders
    /// they work in. Null when none exists, which simply means no refresh happens.
    /// </summary>
    public static string? ResolveProbeDirectory(System.Collections.Generic.IEnumerable<string>? candidateRoots)
    {
        if (candidateRoots is null)
        {
            return null;
        }

        foreach (var root in candidateRoots)
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                return root;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}

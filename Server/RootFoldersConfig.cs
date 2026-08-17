using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Accel.Settings;

namespace Accel.Server;

/// <summary>
/// Phase UI-C / P1-T3: loads the "root folders" config, which can be on disk in either of two
/// shapes:
///
/// <para><b>v1 (legacy):</b> a flat JSON array of folder path strings, e.g.
/// <c>["C:/projects"]</c>. This is the original shape and is still read verbatim.</para>
///
/// <para><b>v2:</b> a JSON object
/// <c>{"version":2,"roots":["C:\\one"],"sessions":{"&lt;sessionId&gt;":{"displayName":"...",
/// "pinned":true,"hidden":false,"lastOpenedUtc":"2026-01-01T00:00:00Z"}}}</c>. <c>sessions</c> is
/// a *sparse override map*, not a session registry - see <see cref="Save"/> for the prune
/// contract.</para>
///
/// <para>The loader is polymorphic on <see cref="JsonValueKind"/>: an array root is parsed as v1
/// (roots only, no sessions); an object root is parsed as v2 (<c>version</c>/<c>roots</c>/
/// <c>sessions</c>). Any other shape, or unparseable JSON, degrades to an empty config - this
/// method never throws.</para>
///
/// <para><b>Probe order (per project-ui.md's "Root folders (`folder.json`)" section,
/// decision 0):</b></para>
/// <list type="number">
/// <item><c>%USERPROFILE%\.claude\accel-folders.json</c> - the durable home, colocated with
/// the <c>accel-state.json</c> that <see cref="Accel.Cli.FileBackedStatusLineChainStore.DefaultPath"/>
/// already writes to <c>%USERPROFILE%\.claude\</c>.</item>
/// <item><c>&lt;directory of the running executable&gt;\folder.json</c>.</item>
/// <item><c>&lt;current working directory&gt;\folder.json</c>.</item>
/// </list>
///
/// <para><b>"First that exists AND parses" - exact semantics used here:</b> candidates are
/// tried strictly in order. The first candidate whose file *exists on disk* is the one that
/// decides the outcome - if it exists but is malformed JSON, or valid JSON that isn't a
/// recognized v1/v2 shape, the result is an empty config immediately; later candidates are
/// never consulted (a user who put a broken config at the durable-home slot almost certainly
/// meant that slot, not the exe-dir or cwd fallback - silently trying the next slot would mask
/// the mistake). Only when a candidate's file does not exist at all do we move on to the next
/// candidate. If none of the three files exist, or the loop falls through, the result is an
/// empty config. This method never throws.</para>
///
/// <para>Paths *inside* a parsed roots array are returned exactly as written in the config file
/// - no <see cref="Path.GetFullPath"/>, no separator normalization - per project-ui.md: any
/// normalization is for internal comparison purposes only (a later phase's job), never for
/// what gets rendered back to a client.</para>
///
/// <para><b>Writing</b> (<see cref="Save"/>) always produces the v2 shape - round-tripping a v1
/// file through a save upgrades it to v2 - and reuses <see cref="SettingsFile"/>'s atomic
/// temp-file-plus-backup write mechanism rather than hand-rolling a second one.</para>
/// </summary>
public static class RootFoldersConfig
{
    /// <summary>File name for the durable-home candidate (candidate 1).</summary>
    public const string DurableFileName = "accel-folders.json";

    /// <summary>File name for the exe-directory and cwd candidates (candidates 2 and 3).</summary>
    public const string LocalFileName = "folder.json";

    /// <summary>The v2 "version" field value written by <see cref="Save"/>.</summary>
    public const int CurrentVersion = 2;

    private static readonly RootFoldersConfigData EmptyConfig =
        new(Array.Empty<string>(), new Dictionary<string, SessionOverride>());

    /// <summary>Loads using the real default candidate paths (see <see cref="DefaultCandidatePaths"/>).</summary>
    public static string[] Load() => Load(DefaultCandidatePaths());

    /// <summary>
    /// The three real candidate paths, in probe order. Exposed separately from
    /// <see cref="Load()"/> so tests can substitute a different candidate list via
    /// <see cref="Load(IReadOnlyList{string})"/> without touching the real filesystem
    /// locations (<c>%USERPROFILE%</c>, the exe directory, the process cwd).
    /// </summary>
    public static string[] DefaultCandidatePaths() => new[]
    {
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            DurableFileName),
        Path.Combine(AppContext.BaseDirectory, LocalFileName),
        Path.Combine(Directory.GetCurrentDirectory(), LocalFileName),
    };

    /// <summary>
    /// Core probe/parse logic, taking an explicit candidate list (in probe order) so tests can
    /// exercise every branch (missing/malformed/valid, at each of the three slots) without
    /// depending on real machine state.
    ///
    /// <para>Compatibility surface: this keeps returning <c>string[]</c> of roots exactly as it
    /// always has, regardless of whether the on-disk file is v1 or v2 - existing call sites
    /// (<c>EventServer</c>, <c>RootsRoutes</c>, <c>RootsTreeBuilder</c>) are unaffected. Use
    /// <see cref="LoadFull(IReadOnlyList{string})"/> to also read the v2 <c>sessions</c> map.</para>
    /// </summary>
    public static string[] Load(IReadOnlyList<string> candidatePaths) => LoadFull(candidatePaths).Roots;

    /// <summary>
    /// Like <see cref="Load()"/> but also exposes the v2 <c>sessions</c> sparse override map
    /// (empty when the on-disk file is v1, missing, or malformed).
    /// </summary>
    public static RootFoldersConfigData LoadFull() => LoadFull(DefaultCandidatePaths());

    /// <summary>Like <see cref="Load(IReadOnlyList{string})"/> but returns the full v1/v2 config.</summary>
    public static RootFoldersConfigData LoadFull(IReadOnlyList<string> candidatePaths)
    {
        foreach (string path in candidatePaths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                string text = File.ReadAllText(path, Encoding.UTF8);
                return ParseConfig(text);
            }
            catch
            {
                // The candidate exists but couldn't even be read (locked, permissions, etc.) -
                // same "found it, but it's broken" outcome as a parse failure: degrade to
                // empty rather than falling through to the next candidate.
                return EmptyConfig;
            }
        }

        return EmptyConfig;
    }

    /// <summary>
    /// Writes <paramref name="roots"/> and <paramref name="sessions"/> to <paramref name="path"/>
    /// in the v2 shape, always (round-tripping a v1 file upgrades it to v2 on save), using
    /// <see cref="SettingsFile"/>'s atomic temp-file-plus-backup write mechanism.
    ///
    /// <para><b>Prune contract:</b> <paramref name="sessions"/> is a sparse override map keyed
    /// by session id, not a session registry - entries are meant to exist only while they say
    /// something non-default about a session that's still around. On every save, any entry
    /// whose key is not present in <paramref name="keepSessionIds"/> is dropped before writing.
    /// Callers are expected to pass the current set of known/live session ids as
    /// <paramref name="keepSessionIds"/>; this is deliberately a simple "prune if not in the
    /// keep-set" rule rather than any notion of TTL or last-write tracking.</para>
    /// </summary>
    public static void Save(
        string path,
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, SessionOverride> sessions,
        IReadOnlySet<string> keepSessionIds)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(keepSessionIds);

        var rootsArray = new JsonArray();
        foreach (var r in roots)
        {
            // Use the JsonValue.Create(object) overload (as HookEntry.cs does for its args
            // array) rather than the generic JsonArray.Add<T> extension - the generic path hits
            // a JsonValueCustomized<T> serialization bug for string elements in this SDK.
            rootsArray.Add(JsonValue.Create(r));
        }

        var sessionsObj = new JsonObject();
        foreach (var (sessionId, overrideEntry) in sessions)
        {
            if (!keepSessionIds.Contains(sessionId))
            {
                // Stale override - the session it referred to is no longer relevant. Prune it
                // rather than letting the sparse map grow unbounded forever.
                continue;
            }

            var entryObj = new JsonObject
            {
                ["pinned"] = overrideEntry.Pinned,
                ["hidden"] = overrideEntry.Hidden,
            };

            if (overrideEntry.DisplayName is not null)
            {
                entryObj["displayName"] = overrideEntry.DisplayName;
            }

            if (overrideEntry.LastOpenedUtc is { } lastOpened)
            {
                entryObj["lastOpenedUtc"] = lastOpened.ToUniversalTime().ToString("o");
            }

            sessionsObj[sessionId] = entryObj;
        }

        var configObj = new JsonObject
        {
            ["version"] = CurrentVersion,
            ["roots"] = rootsArray,
            ["sessions"] = sessionsObj,
        };

        // SettingsFile.Load(path) is used purely as an atomic-write handle here: its Status
        // (Ok/Missing/Empty/Malformed) reflects whatever currently lives at `path` - which may
        // not even be a JSON object (a v1 array file, or nothing) - but Save() below doesn't
        // consult Status; it just takes the .accel.bak snapshot (if a file exists) and does the
        // temp-file-then-replace swap.
        SettingsFile.Load(path).Save(configObj);
    }

    private static RootFoldersConfigData ParseConfig(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            return root.ValueKind switch
            {
                JsonValueKind.Array => new RootFoldersConfigData(
                    ParseRootsArray(root),
                    new Dictionary<string, SessionOverride>()),
                JsonValueKind.Object => ParseV2Object(root),
                _ => EmptyConfig,
            };
        }
        catch (JsonException)
        {
            return EmptyConfig;
        }
    }

    private static RootFoldersConfigData ParseV2Object(JsonElement root)
    {
        string[] roots = root.TryGetProperty("roots", out var rootsElement) && rootsElement.ValueKind == JsonValueKind.Array
            ? ParseRootsArray(rootsElement)
            : Array.Empty<string>();

        var sessions = new Dictionary<string, SessionOverride>();
        if (root.TryGetProperty("sessions", out var sessionsElement) && sessionsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in sessionsElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                sessions[property.Name] = ParseSessionOverride(property.Value);
            }
        }

        return new RootFoldersConfigData(roots, sessions);
    }

    private static SessionOverride ParseSessionOverride(JsonElement obj)
    {
        string? displayName = obj.TryGetProperty("displayName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()
            : null;

        bool pinned = obj.TryGetProperty("pinned", out var pinnedEl) &&
            (pinnedEl.ValueKind == JsonValueKind.True || pinnedEl.ValueKind == JsonValueKind.False) &&
            pinnedEl.GetBoolean();

        bool hidden = obj.TryGetProperty("hidden", out var hiddenEl) &&
            (hiddenEl.ValueKind == JsonValueKind.True || hiddenEl.ValueKind == JsonValueKind.False) &&
            hiddenEl.GetBoolean();

        DateTime? lastOpenedUtc = null;
        if (obj.TryGetProperty("lastOpenedUtc", out var lastOpenedEl) &&
            lastOpenedEl.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(
                lastOpenedEl.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            lastOpenedUtc = parsed;
        }

        return new SessionOverride(displayName, pinned, hidden, lastOpenedUtc);
    }

    private static string[] ParseRootsArray(JsonElement arrayElement)
    {
        var result = new List<string>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                // Not a homogeneous array of strings - the whole roots list is treated as
                // malformed for our purposes.
                return Array.Empty<string>();
            }

            result.Add(item.GetString() ?? string.Empty);
        }

        return result.ToArray();
    }
}

/// <summary>
/// A sparse, per-session override: only the fields a user has explicitly changed away from the
/// default are meaningful. Not a session registry entry - sessions the user has never touched
/// (renamed/pinned/hidden) simply have no entry here at all.
/// </summary>
/// <param name="DisplayName">User-chosen display name override, or <see langword="null"/> for none.</param>
/// <param name="Pinned">Whether the session is pinned to the top of its list.</param>
/// <param name="Hidden">Whether the session is hidden from the default view.</param>
/// <param name="LastOpenedUtc">Last time the user opened this session, if tracked.</param>
public sealed record SessionOverride(string? DisplayName, bool Pinned, bool Hidden, DateTime? LastOpenedUtc);

/// <summary>The full v1/v2 root-folders config: roots (always) plus the v2 sessions override map.</summary>
/// <param name="Roots">Root folder paths, verbatim as written in the config file.</param>
/// <param name="Sessions">Sparse per-session overrides, keyed by session id. Empty for v1 files.</param>
public sealed record RootFoldersConfigData(string[] Roots, IReadOnlyDictionary<string, SessionOverride> Sessions);

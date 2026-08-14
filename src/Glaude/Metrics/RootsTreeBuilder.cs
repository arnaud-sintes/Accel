using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Glaude.Metrics;

/// <summary>
/// Phase UI-D: builds the entire <c>GET /roots/tree</c> document - enumerating every session
/// ever recorded on disk under <c>%USERPROFILE%\.claude\projects\*</c>, attributing each one to
/// a configured root folder by its transcript's own <c>cwd</c> (never by the slug - see
/// project-ui.md's "Root attribution" section for the collision this avoids), merging in live
/// data from <see cref="SessionState"/>, and computing percentages via
/// <see cref="ModelWindowTable"/> for anything not currently live.
///
/// Kept out of <c>Server/RootsTreeRoute.cs</c> deliberately so the scan/merge/caching logic is
/// unit-testable without spinning up a real HTTP server, mirroring how <see cref="SessionState"/>
/// itself is a plain class the route layer merely reads from.
///
/// One instance is meant to live for the lifetime of the running server (see
/// <c>EventServer.RootsTree</c>) so its caches actually help across ticks - constructing a new
/// instance per call defeats the whole point of "Per-tick cost and caching" in project-ui.md.
/// </summary>
public sealed class RootsTreeBuilder
{
    // Head-read results (cwd + first-user-message text) are cached permanently, keyed by
    // absolute file path, once a read successfully yields a cwd - project-ui.md: "cwd and the
    // derived name... are immutable for a session's lifetime once successfully read - cache
    // them permanently, never invalidate/re-read them even if the file's mtime changes later".
    // A read that fails to find a cwd (e.g. a session file that's still just its first couple
    // of lines) is deliberately NOT cached, so it gets retried on a later tick instead of
    // showing "unattributed" forever for what is likely just a race with Claude Code's own
    // writer.
    private readonly ConcurrentDictionary<string, TranscriptHeadInfo> _headCache = new(StringComparer.OrdinalIgnoreCase);

    // Tail-read results (last assistant entry: model/effort/usage) for NOT-currently-live
    // sessions, cached per absolute path keyed on (length, LastWriteTimeUtc) - re-read only
    // when that key changes, per project-ui.md's "Per-tick cost and caching" section.
    private readonly ConcurrentDictionary<string, TailCacheEntry> _tailCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of distinct file paths with a permanently-cached head read. Test hook.</summary>
    public int HeadCacheCount => _headCache.Count;

    /// <summary>Number of distinct file paths with a cached tail read. Test hook.</summary>
    public int TailCacheCount => _tailCache.Count;

    private sealed record TailCacheEntry(long Length, DateTime LastWriteUtc, TranscriptAssistantEntry? Entry);

    /// <summary>
    /// Builds the full tree document for the given configured <paramref name="roots"/> (in the
    /// exact order they should be rendered/sorted - config order), merging in
    /// <paramref name="state"/>'s live session/agent data. <paramref name="projectsDirOverride"/>
    /// lets tests point at a fixture directory instead of the real
    /// <c>%USERPROFILE%\.claude\projects</c> - mirrors how <see cref="Server.RootFoldersConfig"/>
    /// exposes an overridable candidate list for the same reason.
    ///
    /// Never throws: a bad root, a bad slug directory, or a single bad session file each
    /// degrade to "fewer results", never a propagated exception - see project-ui.md's
    /// "Never 500" rule for this route.
    /// </summary>
    public RootsTreeDto Build(string[]? roots, SessionState state, string? projectsDirOverride = null)
    {
        var stopwatch = Stopwatch.StartNew();
        roots ??= Array.Empty<string>();

        string projectsDir = projectsDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects");

        var normalizedRoots = roots
            .Select(r => (Original: r, Normalized: NormalizeForCompare(r)))
            .ToArray();

        var sessionsByRoot = new Dictionary<string, List<SessionTreeDto>>();
        foreach (string root in roots)
        {
            // Duplicate-configured-root defensive: last one wins the bucket, first one for
            // matching purposes below - either way nothing is thrown or dropped.
            sessionsByRoot[root] = new List<SessionTreeDto>();
        }

        var unattributedSessions = new List<SessionTreeDto>();
        var allSessionIds = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            if (Directory.Exists(projectsDir))
            {
                foreach (string slugDir in SafeEnumerateDirectories(projectsDir))
                {
                    string projectDirName = SafeGetFileName(slugDir);

                    // Top-directory-only: subagent transcripts live one level deeper, under
                    // <session_id>\subagents\agent-<id>.jsonl, per project-ui.md's on-disk
                    // layout - a non-recursive listing here already excludes them without any
                    // extra "is this a subagents path" filtering.
                    foreach (string file in SafeEnumerateFiles(slugDir))
                    {
                        SessionTreeDto? sessionDto = null;
                        try
                        {
                            sessionDto = BuildSessionDto(file, projectDirName, state);
                        }
                        catch
                        {
                            // One malformed/locked session file must reduce the result by one
                            // session, never fail the whole scan.
                        }

                        if (sessionDto is null)
                        {
                            continue;
                        }

                        allSessionIds.Add(sessionDto.SessionId);

                        string? matchedRoot = MatchRoot(sessionDto.Cwd, normalizedRoots);
                        if (matchedRoot is not null && sessionsByRoot.TryGetValue(matchedRoot, out var bucket))
                        {
                            bucket.Add(sessionDto);
                        }
                        else
                        {
                            unattributedSessions.Add(sessionDto);
                        }
                    }
                }
            }
        }
        catch
        {
            // Directory enumeration itself failed (permissions, race, etc.) - degrade to
            // whatever was already collected, never throw.
        }

        // Live sub-agents, grouped by parent session id, per project-ui.md: nested only under
        // sessions with is_live == true, and only agents whose own status is "live".
        var liveAgentsByParent = state.GetAllAgents()
            .Where(a => a.Status == AgentStatus.Live)
            .GroupBy(a => a.ParentSessionId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var bucket in sessionsByRoot.Values)
        {
            AttachAgents(bucket, liveAgentsByParent);
        }

        AttachAgents(unattributedSessions, liveAgentsByParent);

        var unattributedAgents = new List<AgentTreeDto>();
        foreach (var (parentId, agents) in liveAgentsByParent)
        {
            if (string.IsNullOrEmpty(parentId) || !allSessionIds.Contains(parentId))
            {
                foreach (var agent in agents)
                {
                    unattributedAgents.Add(ToAgentDto(agent));
                }
            }
        }

        var rootDtos = roots
            .Select(r => new RootTreeDto(
                Path: r,
                Exists: SafeDirectoryExists(r),
                Sessions: SortSessions(sessionsByRoot.TryGetValue(r, out var s) ? s : new List<SessionTreeDto>())))
            .ToArray();

        stopwatch.Stop();

        return new RootsTreeDto(
            Roots: rootDtos,
            UnattributedSessions: SortSessions(unattributedSessions),
            UnattributedAgents: unattributedAgents.OrderByDescending(a => a.AsOf).ToArray(),
            GeneratedAtUtc: DateTime.UtcNow,
            ScanMs: stopwatch.ElapsedMilliseconds);
    }

    private void AttachAgents(List<SessionTreeDto> sessions, Dictionary<string, List<AgentRecord>> liveAgentsByParent)
    {
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (!session.IsLive)
            {
                continue; // Agents already Array.Empty<AgentTreeDto>() from BuildSessionDto.
            }

            if (liveAgentsByParent.TryGetValue(session.SessionId, out var agents) && agents.Count > 0)
            {
                sessions[i] = session with
                {
                    Agents = agents.Select(ToAgentDto).OrderByDescending(a => a.AsOf).ToArray(),
                };
            }
        }
    }

    private SessionTreeDto? BuildSessionDto(string filePath, string projectDirName, SessionState state)
    {
        string sessionId = Path.GetFileNameWithoutExtension(filePath);
        TranscriptHeadInfo headInfo = GetHeadInfoCached(filePath);

        SessionSnapshot? snapshot = null;
        bool isLive = false;
        if (state.TryGetSession(sessionId, out var found) && found is not null)
        {
            snapshot = found;
            isLive = !found.Ended;
        }

        string name;
        string nameSource;
        if (!string.IsNullOrEmpty(snapshot?.SessionName))
        {
            name = snapshot!.SessionName!;
            nameSource = "status_line";
        }
        else
        {
            string? derived = TranscriptHeadReader.DeriveLabel(headInfo.FirstUserMessageText);
            if (!string.IsNullOrEmpty(derived))
            {
                name = derived!;
                nameSource = "first_message";
            }
            else
            {
                name = sessionId.Length <= 12 ? sessionId : sessionId.Substring(0, 12);
                nameSource = "session_id";
            }
        }

        string? modelId;
        string? modelDisplayName = null;
        string? effortLevel;
        long? contextWindowSize;
        bool contextWindowSizeAssumed;
        long? usedTokens;
        double? usedPercentage;
        string source;
        DateTime asOf;
        DateTime lastActivityUtc;

        if (isLive && snapshot is not null)
        {
            modelId = snapshot.ModelId;
            modelDisplayName = snapshot.ModelDisplayName;
            effortLevel = snapshot.EffortLevel;
            source = "statusLine";
            asOf = snapshot.ReceivedAtUtc;
            lastActivityUtc = snapshot.ReceivedAtUtc;

            if (snapshot.ContextWindowSize.HasValue)
            {
                contextWindowSize = snapshot.ContextWindowSize;
                contextWindowSizeAssumed = false;
            }
            else
            {
                // No observed window size on this snapshot (e.g. a minimal placeholder from
                // MarkSessionEnded/never received a full status-line payload) - fall back to
                // the lookup table like the historical path does.
                contextWindowSize = ModelWindowTable.Resolve(modelId);
                contextWindowSizeAssumed = true;
            }

            usedTokens = snapshot.UsedTokens;
            usedPercentage = snapshot.UsedPercentage ?? ComputePercentage(usedTokens, contextWindowSize);
        }
        else
        {
            TranscriptAssistantEntry? tail = GetTailEntryCached(filePath, out DateTime fileLastWriteUtc);
            modelId = tail?.Model;
            effortLevel = tail?.EffortLevel;
            source = "transcript";
            asOf = fileLastWriteUtc;
            lastActivityUtc = fileLastWriteUtc;

            // Per project-ui.md: a historical reading always comes from the lookup table,
            // never an observed value - there is no contextWindowSize field in a transcript.
            contextWindowSize = ModelWindowTable.Resolve(modelId);
            contextWindowSizeAssumed = true;

            if (tail is not null)
            {
                long used = (long)tail.InputTokens + tail.CacheCreationInputTokens + tail.CacheReadInputTokens;
                usedTokens = used;
                usedPercentage = ComputePercentage(used, contextWindowSize);
            }
            else
            {
                usedTokens = null;
                usedPercentage = null;
            }
        }

        return new SessionTreeDto(
            SessionId: sessionId,
            Name: name,
            NameSource: nameSource,
            Cwd: headInfo.Cwd,
            ProjectDir: projectDirName,
            IsLive: isLive,
            Status: isLive ? "live" : "ended",
            ModelId: modelId,
            ModelDisplayName: modelDisplayName,
            EffortLevel: effortLevel,
            ContextWindowSize: contextWindowSize,
            ContextWindowSizeAssumed: contextWindowSizeAssumed,
            UsedTokens: usedTokens,
            UsedPercentage: usedPercentage,
            Source: source,
            AsOf: asOf,
            LastActivityUtc: lastActivityUtc,
            Agents: Array.Empty<AgentTreeDto>());
    }

    private AgentTreeDto ToAgentDto(AgentRecord record)
    {
        int tableWindow = ModelWindowTable.Resolve(record.ModelId, out bool matched);
        int windowSize = record.ContextWindowSize > 0 ? record.ContextWindowSize : tableWindow;

        // Per project-ui.md: "assumed" means the number came from ModelWindowTable rather
        // than an observed value. AgentRecord does not itself preserve whether its
        // ContextWindowSize came from an observed `contextWindowSize` field or the table
        // fallback, so this uses ModelWindowTable's own exact/prefix-vs-default match
        // classification for the agent's model id as the best available signal.
        bool assumed = !matched;

        long usedTokens = (long)record.InputTokens + record.CacheCreationInputTokens + record.CacheReadInputTokens;
        double? usedPercentage = ComputePercentage(usedTokens, windowSize);

        return new AgentTreeDto(
            AgentId: record.AgentId,
            Name: record.Name,
            AgentType: record.AgentType,
            ModelId: record.ModelId,
            EffortLevel: record.EffortLevel,
            InputTokens: record.InputTokens,
            OutputTokens: record.OutputTokens,
            CacheCreationInputTokens: record.CacheCreationInputTokens,
            CacheReadInputTokens: record.CacheReadInputTokens,
            ContextWindowSize: windowSize,
            ContextWindowSizeAssumed: assumed,
            UsedPercentage: usedPercentage,
            Status: StatusToString(record.Status),
            Source: record.Source,
            AsOf: record.ReceivedAtUtc);
    }

    private static string StatusToString(AgentStatus status) => status switch
    {
        AgentStatus.Live => "live",
        AgentStatus.Ended => "ended",
        AgentStatus.Stale => "stale",
        _ => "live",
    };

    private TranscriptHeadInfo GetHeadInfoCached(string path)
    {
        if (_headCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        TranscriptHeadInfo info = TranscriptHeadReader.Read(path);
        if (info.Cwd is not null)
        {
            // Permanent cache entry - see class summary. A miss (null cwd) is intentionally
            // not cached so a not-yet-fully-written file gets retried on a later tick.
            _headCache[path] = info;
        }

        return info;
    }

    private TranscriptAssistantEntry? GetTailEntryCached(string path, out DateTime lastWriteTimeUtc)
    {
        long length;
        DateTime mtime;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                lastWriteTimeUtc = default;
                return null;
            }

            length = fi.Length;
            mtime = fi.LastWriteTimeUtc;
        }
        catch
        {
            lastWriteTimeUtc = default;
            return null;
        }

        lastWriteTimeUtc = mtime;

        if (_tailCache.TryGetValue(path, out var cached) && cached.Length == length && cached.LastWriteUtc == mtime)
        {
            return cached.Entry;
        }

        TranscriptAssistantEntry? entry = TranscriptReader.TryReadLastAssistantEntry(path);
        _tailCache[path] = new TailCacheEntry(length, mtime, entry);
        return entry;
    }

    private static double? ComputePercentage(long? usedTokens, long? windowSize)
    {
        if (!usedTokens.HasValue || !windowSize.HasValue || windowSize.Value <= 0)
        {
            return null;
        }

        return Math.Round((double)usedTokens.Value / windowSize.Value * 100.0, 1);
    }

    private static SessionTreeDto[] SortSessions(IEnumerable<SessionTreeDto> sessions) => sessions
        .OrderByDescending(s => s.IsLive)
        .ThenByDescending(s => s.LastActivityUtc)
        .ToArray();

    private static string? MatchRoot(string? cwd, (string Original, string Normalized)[] roots)
    {
        if (string.IsNullOrEmpty(cwd))
        {
            return null;
        }

        string normalizedCwd = NormalizeForCompare(cwd);

        string? best = null;
        int bestLength = -1;

        foreach (var (original, normalized) in roots)
        {
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            if (IsSameOrDescendant(normalizedCwd, normalized) && normalized.Length > bestLength)
            {
                best = original;
                bestLength = normalized.Length;
            }
        }

        return best;
    }

    // Segment-boundary comparison: "C:\projects" must not capture "C:\projects-foo" - only an
    // exact match or a match followed immediately by a separator counts.
    private static bool IsSameOrDescendant(string cwd, string root)
    {
        if (string.Equals(cwd, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string rootWithSeparator = root + Path.DirectorySeparatorChar;
        return cwd.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForCompare(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        try
        {
            string full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd('\\', '/');
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string SafeGetFileName(string path)
    {
        try
        {
            return Path.GetFileName(path) ?? path;
        }
        catch
        {
            return path;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>One session row inside <see cref="RootsTreeDto"/> - see project-ui.md's example.</summary>
public sealed record SessionTreeDto(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("name_source")] string NameSource,
    [property: JsonPropertyName("cwd")] string? Cwd,
    [property: JsonPropertyName("project_dir")] string ProjectDir,
    [property: JsonPropertyName("is_live")] bool IsLive,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("model_id")] string? ModelId,
    [property: JsonPropertyName("model_display_name")] string? ModelDisplayName,
    [property: JsonPropertyName("effort_level")] string? EffortLevel,
    [property: JsonPropertyName("context_window_size")] long? ContextWindowSize,
    [property: JsonPropertyName("context_window_size_assumed")] bool ContextWindowSizeAssumed,
    [property: JsonPropertyName("used_tokens")] long? UsedTokens,
    [property: JsonPropertyName("used_percentage")] double? UsedPercentage,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("as_of")] DateTime AsOf,
    [property: JsonPropertyName("last_activity_utc")] DateTime LastActivityUtc,
    [property: JsonPropertyName("agents")] AgentTreeDto[] Agents);

/// <summary>One live sub-agent row nested under a live <see cref="SessionTreeDto"/>.</summary>
public sealed record AgentTreeDto(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("agent_type")] string? AgentType,
    [property: JsonPropertyName("model_id")] string? ModelId,
    [property: JsonPropertyName("effort_level")] string? EffortLevel,
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens,
    [property: JsonPropertyName("cache_creation_input_tokens")] int CacheCreationInputTokens,
    [property: JsonPropertyName("cache_read_input_tokens")] int CacheReadInputTokens,
    [property: JsonPropertyName("context_window_size")] int ContextWindowSize,
    [property: JsonPropertyName("context_window_size_assumed")] bool ContextWindowSizeAssumed,
    [property: JsonPropertyName("used_percentage")] double? UsedPercentage,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("as_of")] DateTime AsOf);

/// <summary>One configured root folder's node, per project-ui.md's example.</summary>
public sealed record RootTreeDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("sessions")] SessionTreeDto[] Sessions);

/// <summary>The whole <c>GET /roots/tree</c> document.</summary>
public sealed record RootsTreeDto(
    [property: JsonPropertyName("roots")] RootTreeDto[] Roots,
    [property: JsonPropertyName("unattributed_sessions")] SessionTreeDto[] UnattributedSessions,
    [property: JsonPropertyName("unattributed_agents")] AgentTreeDto[] UnattributedAgents,
    [property: JsonPropertyName("generated_at_utc")] DateTime GeneratedAtUtc,
    [property: JsonPropertyName("scan_ms")] long ScanMs);

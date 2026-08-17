using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Accel.Metrics;

/// <summary>
/// The fields Accel extracts from the *head* of a transcript JSONL file: the session's
/// starting <c>cwd</c>, the raw text of the first "real" user message (see
/// <see cref="TranscriptHeadReader.DeriveLabel"/> for how that text becomes a display label),
/// and (see section 6.2 of claude-agentgraph.md) the first parseable top-level <c>timestamp</c>
/// found scanning forward through the head window - a durable, immutable-per-file "session
/// started at" signal, in the same spirit as <see cref="Cwd"/>.
/// </summary>
public sealed record TranscriptHeadInfo(string? Cwd, string? FirstUserMessageText, DateTime? FirstTimestampUtc = null);

/// <summary>
/// Bounded head-reader for transcript JSONL files. Reads only the first ~64KB of the file
/// (mirrors <see cref="TranscriptReader"/>'s tail-read, just at the other end) and parses
/// whatever complete lines land inside that window to extract:
///
/// 1. The session's starting <c>cwd</c> - the first top-level <c>"cwd"</c> string property
///    found scanning forward through parsed lines. Per project-ui.md, the very first line on
///    this machine is typically a mode-marker with no <c>cwd</c>, so this must scan past it
///    rather than assume the first line carries it.
/// 2. The first "real" user message text - the first <c>"type":"user"</c> entry whose
///    <c>message.content</c> is a plain string, or an array whose first element is
///    <c>{"type":"text","text":"..."}</c>, skipping wrapper entries (command markers, system
///    reminders, interruption notices) per decision 2 in project-ui.md.
///
/// Never throws: missing file, empty file, malformed JSON on some/all lines, a partially-
/// written trailing line, and fields of unexpected JSON types all degrade to null rather than
/// propagate, per project.md's "every field optional... must never throw" requirement.
/// </summary>
public static class TranscriptHeadReader
{
    private const int HeadBytes = 64 * 1024;
    private const int MaxUserCandidates = 20;

    private static readonly string[] SkipPrefixes =
    {
        "<command-message>",
        "<command-name>",
        "<local-command-",
        "<system-reminder>",
        "[Request interrupted",

        // Bug-fix pass (UI-H): when a user invokes a skill (e.g. "/caveman"), Claude Code
        // injects the skill's full body text as a SEPARATE, SUBSEQUENT "type":"user" entry
        // immediately after the <command-message>/<command-name> entry - plain text, not
        // wrapped in any of the prefixes above, so it used to win as the session's "first
        // real user message" label (e.g. producing the junk name "Base directory for this
        // skill:" instead of the user's actual first request). Verified against real
        // transcripts on this machine that exhibit exactly this sequence, e.g.
        // C:\Users\a.sintes\.claude\projects\C--projects\3e7a5e3e-3210-41ef-be36-3604b2b101a7.jsonl
        // (line 4: <command-message>caveman</command-message><command-name>/caveman<...> ->
        // line 5, its direct child by parentUuid, isMeta:true, text starting with "Base
        // directory for this skill:" -> the genuine first user request only appears later,
        // at line 17: "Focus on swgen2 repository, backend folder...."). Every injected
        // skill body observed on this machine starts with this exact literal string, so a
        // 6th literal skip prefix is the simplest fix that matches the real case without
        // needing to track parentUuid chains.
        "Base directory for this skill:",
    };

    /// <summary>
    /// Reads the head window of the transcript at <paramref name="path"/> and extracts the
    /// starting cwd and first real user message text. Returns a result with both fields null
    /// (never a null result itself, and never a thrown exception) if the path is missing/empty
    /// or nothing usable is found within the head window.
    /// </summary>
    public static TranscriptHeadInfo Read(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return new TranscriptHeadInfo(null, null);
        }

        string head;
        bool truncatedByBound;

        try
        {
            if (!File.Exists(path))
            {
                return new TranscriptHeadInfo(null, null);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0)
            {
                return new TranscriptHeadInfo(null, null);
            }

            truncatedByBound = stream.Length > HeadBytes;
            int toRead = (int)Math.Min(HeadBytes, stream.Length);
            var buffer = new byte[toRead];
            int totalRead = 0;
            while (totalRead < toRead)
            {
                int read = stream.Read(buffer, totalRead, toRead - totalRead);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            head = Encoding.UTF8.GetString(buffer, 0, totalRead);
        }
        catch
        {
            // Missing/locked/inaccessible file, I/O error, etc. - "no data", never throw.
            return new TranscriptHeadInfo(null, null);
        }

        if (string.IsNullOrEmpty(head))
        {
            return new TranscriptHeadInfo(null, null);
        }

        bool endsWithNewline = head.EndsWith('\n');
        string[] rawLines = head.Split('\n');

        // Discard a trailing partial/incomplete line - mirrors TranscriptReader's handling of
        // a trailing partial line at the tail, just applied at the head-window's far edge: if
        // the buffer was truncated by the head bound and doesn't end on a line boundary, the
        // last line in the buffer may have been cut off mid-write/mid-read and is not safe to
        // parse. If the buffer wasn't truncated (whole file fit within the head window) then
        // even a last line without a trailing newline is genuinely complete (end of file).
        int lastUsableIndex = (endsWithNewline || !truncatedByBound)
            ? rawLines.Length - 1
            : rawLines.Length - 2;

        string? cwd = null;
        DateTime? firstTimestampUtc = null;
        var userLines = new List<string>();

        for (int i = 0; i <= lastUsableIndex && i < rawLines.Length; i++)
        {
            string line = rawLines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument? doc = TryParseLine(line);
            if (doc is null)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (cwd is null
                    && root.TryGetProperty("cwd", out var cwdProp)
                    && cwdProp.ValueKind == JsonValueKind.String)
                {
                    cwd = cwdProp.GetString();
                }

                // "First parseable timestamp wins", scanning forward - not "the first line's
                // timestamp" - for the same reason the cwd probe above scans forward: the very
                // first line on this machine is typically a mode-marker with no such field.
                if (firstTimestampUtc is null
                    && root.TryGetProperty("timestamp", out var tsProp)
                    && tsProp.ValueKind == JsonValueKind.String
                    && TryParseIso8601Utc(tsProp.GetString(), out DateTime parsedTimestamp))
                {
                    firstTimestampUtc = parsedTimestamp;
                }

                if (userLines.Count < MaxUserCandidates
                    && root.TryGetProperty("type", out var typeProp)
                    && typeProp.ValueKind == JsonValueKind.String
                    && typeProp.GetString() == "user")
                {
                    userLines.Add(line);
                }
            }
        }

        string? firstUserMessageText = null;

        foreach (string userLine in userLines)
        {
            string? candidate = TryExtractUserText(userLine);
            if (candidate is null)
            {
                continue;
            }

            if (StartsWithAnySkipPrefix(candidate))
            {
                continue;
            }

            firstUserMessageText = candidate;
            break;
        }

        return new TranscriptHeadInfo(cwd, firstUserMessageText, firstTimestampUtc);
    }

    /// <summary>
    /// Tolerant ISO-8601 -&gt; UTC parse used for a transcript line's <c>timestamp</c> field:
    /// never throws (returns false on anything unparseable), normalizes any offset to UTC, and
    /// rejects results outside a sanity window (<c>[2020-01-01, now + 1 day]</c>) as a
    /// clock-skew / junk-line guard - out-of-range degrades to "no timestamp", exactly like a
    /// malformed string does.
    /// </summary>
    /// <summary>Internal (not private) so <see cref="MetricsPipeline"/>'s tier-2
    /// <c>GetTaskDateTime</c> helper can reuse the exact same tolerant parse/sanity-gate logic
    /// rather than duplicating it - see claude-agentgraph.md section 6.3.</summary>
    internal static bool TryParseIso8601Utc(string? s, out DateTime utc)
    {
        utc = default;

        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        // Note: claude-agentgraph.md section 6.2 suggested combining RoundtripKind with
        // AdjustToUniversal, but .NET rejects that combination at runtime
        // (DateTimeStyles.RoundtripKind cannot be used with AdjustToUniversal) - AdjustToUniversal
        // alone already normalizes any parsed offset (including "Z") to UTC, which is all this
        // needs.
        if (!DateTime.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal,
                out DateTime parsed))
        {
            return false;
        }

        if (parsed.Kind != DateTimeKind.Utc)
        {
            parsed = parsed.ToUniversalTime();
        }

        DateTime sanityFloor = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime sanityCeiling = DateTime.UtcNow.AddDays(1);
        if (parsed < sanityFloor || parsed > sanityCeiling)
        {
            return false;
        }

        utc = parsed;
        return true;
    }

    private static bool StartsWithAnySkipPrefix(string text)
    {
        foreach (string prefix in SkipPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonDocument? TryParseLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch
        {
            // Malformed / partial JSON on this line - skip it, keep going.
            return null;
        }
    }

    private static string? TryExtractUserText(string userLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(userLine);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content))
            {
                return null;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            if (content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0)
            {
                var first = content[0];
                if (first.ValueKind == JsonValueKind.Object
                    && first.TryGetProperty("type", out var blockType)
                    && blockType.ValueKind == JsonValueKind.String
                    && blockType.GetString() == "text"
                    && first.TryGetProperty("text", out var textProp)
                    && textProp.ValueKind == JsonValueKind.String)
                {
                    return textProp.GetString();
                }

                return null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Derives a display label from a raw first-user-message text: collapses all whitespace
    /// (including newlines/tabs) to single spaces, strips control characters, then truncates
    /// to 60 characters at a word boundary. Returns null if the input is null, empty, or
    /// whitespace-only.
    /// </summary>
    public static string? DeriveLabel(string? firstUserMessageText)
    {
        if (string.IsNullOrWhiteSpace(firstUserMessageText))
        {
            return null;
        }

        var sb = new StringBuilder(firstUserMessageText.Length);
        bool lastWasSpace = false;

        foreach (char c in firstUserMessageText)
        {
            if (char.IsControl(c))
            {
                // Treat control characters (including \n, \r, \t) as whitespace for the
                // purposes of collapsing, but otherwise strip them outright.
                if (c is '\n' or '\r' or '\t')
                {
                    if (!lastWasSpace && sb.Length > 0)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
        }

        string collapsed = sb.ToString().Trim();

        if (string.IsNullOrEmpty(collapsed))
        {
            return null;
        }

        if (collapsed.Length <= 60)
        {
            return collapsed;
        }

        string truncated = collapsed.Substring(0, 60);
        int lastSpace = truncated.LastIndexOf(' ');

        return lastSpace > 0 ? truncated.Substring(0, lastSpace) : truncated;
    }
}

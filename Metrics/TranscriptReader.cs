using System.Text;
using System.Text.Json;

namespace Accel.Metrics;

/// <summary>
/// The fields Accel extracts from the newest "type":"assistant" entry in a transcript
/// JSONL file (either a subagent's <c>agent_transcript_path</c> or a main session's
/// <c>transcript_path</c> - both share the same on-disk entry shape). See project.md,
/// "Model/Effort/Context metrics sourcing" -> "Source 4 - transcript JSONL".
/// </summary>
public sealed record TranscriptAssistantEntry(
    string? Model,
    string? EffortLevel,
    int InputTokens,
    int OutputTokens,
    int CacheCreationInputTokens,
    int CacheReadInputTokens);

/// <summary>Where a session's current turn stands according to the end of its transcript.</summary>
public enum TranscriptTurnState
{
    /// <summary>No readable transcript, or nothing in the tail window says either way.</summary>
    Unknown,

    /// <summary>A turn is in progress: the newest conversation entry is a prompt, a tool call or a tool result.</summary>
    Working,

    /// <summary>The last turn is over and the session is waiting for the next prompt.</summary>
    Idle,
}

/// <summary>Everything the tree builder wants from one read of a transcript's tail window.</summary>
public sealed record TranscriptTailSummary(
    TranscriptAssistantEntry? LastAssistant,
    string? AiTitle,
    TranscriptTurnState Turn)
{
    public static readonly TranscriptTailSummary Empty = new(null, null, TranscriptTurnState.Unknown);
}

/// <summary>
/// Bounded tail-reader for transcript JSONL files. Reads only the last ~64KB of the file
/// (these can grow large - a full read on every SubagentStop would not scale) and parses
/// whatever complete lines land inside that window, returning the last parseable
/// "type":"assistant" entry.
///
/// Never throws: missing file, empty file, malformed JSON on some/all lines, and a
/// partially-written trailing line (the file may be actively appended to by Claude Code
/// while Accel reads it) are all handled by returning null / skipping the offending line,
/// per project.md's "every field optional... must never throw" requirement.
/// </summary>
public static class TranscriptReader
{
    private const int TailBytes = 64 * 1024;

    /// <summary>
    /// Returns the last "type":"assistant" entry found within the tail window, or null if
    /// the path is missing/empty/null, the file is empty, or no assistant entry could be
    /// parsed out of the tail window.
    /// </summary>
    public static TranscriptAssistantEntry? TryReadLastAssistantEntry(string? path)
    {
        if (!TryReadTailLines(path, out string[] rawLines, out int firstUsableIndex, out int lastUsableIndex))
        {
            return null;
        }

        TranscriptAssistantEntry? last = null;

        for (int i = firstUsableIndex; i <= lastUsableIndex; i++)
        {
            if (i < 0 || i >= rawLines.Length)
            {
                continue;
            }

            var entry = TryParseAssistantLine(rawLines[i]);
            if (entry is not null)
            {
                last = entry;
            }
        }

        return last;
    }

    /// <summary>
    /// Returns the last <c>{"type":"ai-title","aiTitle":"..."}</c> entry's <c>aiTitle</c> text
    /// found within the same bounded tail window used by <see cref="TryReadLastAssistantEntry"/>
    /// - "last one wins" when multiple ai-title lines appear in the transcript (verified against
    /// real transcripts on this machine, which re-emit the same/updated ai-title line repeatedly
    /// through the file rather than just once near the top). Never throws; returns null for a
    /// missing/empty file, no ai-title line in the tail window, or a malformed one.
    /// </summary>
    public static string? TryReadLastAiTitle(string? path)
    {
        if (!TryReadTailLines(path, out string[] rawLines, out int firstUsableIndex, out int lastUsableIndex))
        {
            return null;
        }

        string? last = null;

        for (int i = firstUsableIndex; i <= lastUsableIndex; i++)
        {
            if (i < 0 || i >= rawLines.Length)
            {
                continue;
            }

            string? title = TryParseAiTitleLine(rawLines[i]);
            if (title is not null)
            {
                last = title;
            }
        }

        return last;
    }

    /// <summary>
    /// One read, one JSON parse per line, for everything the tree builder needs from a transcript's tail:
    /// the newest assistant entry, the newest ai-title and the state of the current turn.
    ///
    /// <para><b>Turn state</b> is the state implied by the newest <i>conversation</i> entry in the window; the
    /// bookkeeping entries Claude Code interleaves (<c>api-request</c>, <c>attachment</c>, <c>mode</c>,
    /// <c>ai-title</c>, sidechain entries, local slash-command echoes ...) are skipped. A turn is over after an
    /// assistant entry that is not asking for a tool (<c>stop_reason</c> <c>end_turn</c> and the like), after the
    /// <c>turn_duration</c>/<c>stop_hook_summary</c> system entries that follow it, and after the
    /// "[Request interrupted by user]" entry an Esc leaves behind. It is in progress after a prompt, a tool call
    /// or a tool result - the model owes the next step. This is what makes a long tool run read as working and an
    /// interrupted turn read as idle, which no timer could tell apart.</para>
    /// </summary>
    public static TranscriptTailSummary ReadTailSummary(string? path)
    {
        if (!TryReadTailLines(path, out string[] rawLines, out int firstUsableIndex, out int lastUsableIndex))
        {
            return TranscriptTailSummary.Empty;
        }

        TranscriptAssistantEntry? lastAssistant = null;
        string? aiTitle = null;
        var turn = TranscriptTurnState.Unknown;

        for (int i = firstUsableIndex; i <= lastUsableIndex; i++)
        {
            if (i < 0 || i >= rawLines.Length || string.IsNullOrWhiteSpace(rawLines[i]))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(rawLines[i]);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var typeProp)
                    || typeProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? type = typeProp.GetString();
                if (type == "assistant")
                {
                    lastAssistant = ParseAssistant(root);
                }
                else if (type == "ai-title")
                {
                    aiTitle = ParseAiTitle(root) ?? aiTitle;
                }

                if (type is not null && ClassifyTurn(root, type) is { } state)
                {
                    turn = state;
                }
            }
            catch
            {
                // Malformed / partial JSON on this line - skip it, keep going.
            }
        }

        return new TranscriptTailSummary(lastAssistant, aiTitle, turn);
    }

    /// <summary>The turn state one entry implies, or null when the entry says nothing about it.</summary>
    private static TranscriptTurnState? ClassifyTurn(JsonElement root, string type)
    {
        // A sub-agent's own entries (if they ever appear in a main transcript) are not the main turn.
        if (root.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        switch (type)
        {
            case "assistant":
            {
                string? stop = null;
                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("stop_reason", out var stopProp) && stopProp.ValueKind == JsonValueKind.String)
                {
                    stop = stopProp.GetString();
                }

                // No stop reason yet means the entry is still being streamed; a tool_use is a call whose
                // result (or permission answer) is still to come.
                return stop is null or "tool_use" or "pause_turn" ? TranscriptTurnState.Working : TranscriptTurnState.Idle;
            }

            case "user":
            {
                if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
                {
                    return null;
                }

                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out var content))
                {
                    return TranscriptTurnState.Working;
                }

                string? text = null;
                if (content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString();
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var blockType))
                        {
                            continue;
                        }

                        if (blockType.ValueKind == JsonValueKind.String && blockType.GetString() == "tool_result")
                        {
                            return TranscriptTurnState.Working; // the model now owes its next step
                        }

                        if (text is null && blockType.ValueKind == JsonValueKind.String && blockType.GetString() == "text"
                            && block.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                        {
                            text = textProp.GetString();
                        }
                    }
                }

                if (text is not null)
                {
                    if (text.StartsWith("[Request interrupted by user", StringComparison.Ordinal))
                    {
                        return TranscriptTurnState.Idle; // Esc: no Stop hook fires, but this entry is written
                    }

                    // Echoes of built-in slash commands (/clear, /model ...): no model turn follows.
                    if (text.StartsWith("<command-name>", StringComparison.Ordinal)
                        || text.StartsWith("<command-message>", StringComparison.Ordinal)
                        || text.StartsWith("<local-command-", StringComparison.Ordinal))
                    {
                        return null;
                    }
                }

                return TranscriptTurnState.Working; // a prompt
            }

            case "system":
            {
                string? subtype = root.TryGetProperty("subtype", out var subtypeProp) && subtypeProp.ValueKind == JsonValueKind.String
                    ? subtypeProp.GetString()
                    : null;
                return subtype is "turn_duration" or "stop_hook_summary" ? TranscriptTurnState.Idle : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Shared tail-window read: opens <paramref name="path"/>, reads the last ~64KB, and splits
    /// it into raw lines with the same "discard a possibly-partial boundary line" handling used
    /// by every tail-window consumer. Returns <see langword="false"/> (with empty/default out
    /// values) for a missing/empty/inaccessible file - never throws.
    /// </summary>
    private static bool TryReadTailLines(string? path, out string[] rawLines, out int firstUsableIndex, out int lastUsableIndex)
    {
        rawLines = Array.Empty<string>();
        firstUsableIndex = 0;
        lastUsableIndex = -1;

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string tail;
        bool startedMidFile;

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = stream.Length;
            if (length == 0)
            {
                return false;
            }

            long start = Math.Max(0, length - TailBytes);
            startedMidFile = start > 0;

            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            tail = reader.ReadToEnd();
        }
        catch
        {
            // Missing/locked/inaccessible file, I/O error, etc. - "no data", never throw.
            return false;
        }

        if (string.IsNullOrEmpty(tail))
        {
            return false;
        }

        bool endsWithNewline = tail.EndsWith('\n');
        rawLines = tail.Split('\n');

        firstUsableIndex = startedMidFile ? 1 : 0;
        lastUsableIndex = endsWithNewline ? rawLines.Length - 1 : rawLines.Length - 2;
        return true;
    }

    private static TranscriptAssistantEntry? TryParseAssistantLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String
                || typeProp.GetString() != "assistant")
            {
                return null;
            }

            return ParseAssistant(root);
        }
        catch
        {
            // Malformed / partial JSON on this line - skip it, keep going.
            return null;
        }
    }

    /// <summary>The fields of an already-identified <c>"type":"assistant"</c> entry.</summary>
    private static TranscriptAssistantEntry ParseAssistant(JsonElement root)
    {
        string? model = null;
        int input = 0, output = 0, cacheCreate = 0, cacheRead = 0;

        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            if (message.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
            {
                model = modelProp.GetString();
            }

            if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = GetIntOrZero(usage, "input_tokens");
                output = GetIntOrZero(usage, "output_tokens");
                cacheCreate = GetIntOrZero(usage, "cache_creation_input_tokens");
                cacheRead = GetIntOrZero(usage, "cache_read_input_tokens");
            }
        }

        string? effortLevel = null;
        if (root.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.Object)
        {
            if (effort.TryGetProperty("level", out var levelProp) && levelProp.ValueKind == JsonValueKind.String)
            {
                effortLevel = levelProp.GetString();
            }
        }

        return new TranscriptAssistantEntry(model, effortLevel, input, output, cacheCreate, cacheRead);
    }

    /// <summary>The title of an already-identified <c>"type":"ai-title"</c> entry, or null when empty/absent.</summary>
    private static string? ParseAiTitle(JsonElement root)
    {
        // Real on-disk shape (verified against real transcripts on this machine):
        // {"type":"ai-title","aiTitle":"...","sessionId":"..."}.
        if (root.TryGetProperty("aiTitle", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
        {
            string? title = titleProp.GetString();
            return string.IsNullOrEmpty(title) ? null : title;
        }

        return null;
    }

    private static string? TryParseAiTitleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String
                || typeProp.GetString() != "ai-title")
            {
                return null;
            }

            return ParseAiTitle(root);
        }
        catch
        {
            // Malformed / partial JSON on this line - skip it, keep going.
            return null;
        }
    }

    private static int GetIntOrZero(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out int value))
        {
            return value;
        }

        return 0;
    }
}

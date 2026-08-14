using System.Text;
using System.Text.Json;

namespace Glaude.Metrics;

/// <summary>
/// The fields Glaude extracts from the newest "type":"assistant" entry in a transcript
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

/// <summary>
/// Bounded tail-reader for transcript JSONL files. Reads only the last ~64KB of the file
/// (these can grow large - a full read on every SubagentStop would not scale) and parses
/// whatever complete lines land inside that window, returning the last parseable
/// "type":"assistant" entry.
///
/// Never throws: missing file, empty file, malformed JSON on some/all lines, and a
/// partially-written trailing line (the file may be actively appended to by Claude Code
/// while Glaude reads it) are all handled by returning null / skipping the offending line,
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
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string tail;
        bool startedMidFile;

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = stream.Length;
            if (length == 0)
            {
                return null;
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
            return null;
        }

        if (string.IsNullOrEmpty(tail))
        {
            return null;
        }

        bool endsWithNewline = tail.EndsWith('\n');
        string[] rawLines = tail.Split('\n');

        int firstUsableIndex = startedMidFile ? 1 : 0;
        int lastUsableIndex = endsWithNewline ? rawLines.Length - 1 : rawLines.Length - 2;

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
